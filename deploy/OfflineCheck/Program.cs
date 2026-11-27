using System.IO.Compression;
using System.Text.Json;
using NPOI.XSSF.UserModel;
using OxyPlot;
using OxyPlot.Series;
using PumpSelectionTools;
using ZTools.Offline;
using ZTools.Standalone;

if (args.Length == 0) throw new ArgumentException("Pass a test project directory.");
var directory = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
var routes = new OfflineRoutes(directory);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args.Length == 2 && args[1] == "--ui-bridge")
{
    while (Console.ReadLine() is { } line)
    {
        using var request = JsonDocument.Parse(line);
        var input = request.RootElement;
        var id = input.GetProperty("id").GetInt32();
        try
        {
            var result = await routes.Dispatch(input.GetProperty("path").GetString()!, input.GetProperty("data"),
                input.GetProperty("save").GetBoolean());
            if (result is WorkbenchDownload file)
            {
                using (file) result = new { saved = true, file.Name, size = file.Content.Length };
            }
            Console.WriteLine(MobileBridge.Reply(id, result));
        }
        catch (Exception error) { Console.WriteLine(MobileBridge.Failure(id, error)); }
    }
    return;
}
Task<object> Call(string path, object? data = null, bool save = false) =>
    routes.Dispatch("/api/" + path, JsonSerializer.SerializeToElement(data, json), save);
Task<object> Submit(string command, object? data = null) => Call("submissions/command/" + command, data);

var appVersion = (WorkbenchVersion)await Call("version");
Check(Version.TryParse(appVersion.Version, out _) && int.TryParse(appVersion.BuildNumber, out var build) && build > 0 &&
    appVersion.Notes.Contains("## " + appVersion.Version, StringComparison.Ordinal),
    "The offline version log disagrees with the build or release notes.");

var bb2 = Bb2Catalog.Records;
const string kbbCurve = "BB2_LO_KBB_380-150x2_316";
const string heldHddCurve = "BB2_LO_HDD_180-440x3_315";
Check(bb2.Count == 51 && bb2.Count(r => r.AvailableForSelection) == 36 &&
    bb2.Count(r => r.Family == "HDD") == 49 && bb2.Count(r => r.Family == "KBB") == 2 &&
    bb2.All(r => r.Series == "XDB") &&
    bb2.Sum(r => r.Traces.Sum(t => t.Segments.Sum(s => s.Length))) == 14440 &&
    Enumerable.Range(1, 51).All(n => bb2.Count(r => r.PreviousCurveNumber == $"BB2_LO_{n}" &&
        r.CurveNumber == $"BB2_LO_{r.Family}_{r.Size}_{266 + n}") == 1),
    "The BB2 rows, trace points, review flags, or XDB curve numbers were lost.");
var bb2Workbench = new PumpWorkbench();
var lowFlowDuty = new PumpRequest(5, 50, Density: 1110, Hz: 50, Poles: 2, Family: "X", ViscosityCp: 1);
var lowFlowHit = bb2Workbench.Search(lowFlowDuty).Single(pump => pump.Curve == "OH_700");
Check(lowFlowHit is { Size: "25-200", Series: "X", Rpm: 2950, MinDiameter: 130, MaxDiameter: 209,
        Diameter: > 184 and < 186, Clearance: "API" } && Math.Abs(lowFlowHit.Head - 50) < 1e-6,
    "The X 25-200 did not match the screenshot's 5 m3/h and 50 m duty.");
var comparisonDuty = lowFlowDuty with { Rpm = 2975 };
var comparisonHit = bb2Workbench.Search(comparisonDuty).Single(pump => pump.Curve == "OH_700");
Check(comparisonHit is { Diameter: > 183 and < 183.5, Npshr: > .335 and < .337 } &&
    Math.Abs(comparisonHit.Head - 50) < 1e-6,
    "X 25-200 trim and NPSHr did not match the HydCalc speed comparison.");
var lowFlowReport = JsonSerializer.SerializeToElement(bb2Workbench.Curves(new(lowFlowDuty, lowFlowHit.Index)), json);
Check(lowFlowReport.GetProperty("report").GetProperty("mcsfFlow").GetDouble() is > 4.35 and < 4.5 &&
    !bb2Workbench.Search(lowFlowDuty with { Flow = 4.7 }).Any(pump => pump.Curve == "OH_700") &&
    !bb2Workbench.Search(comparisonDuty with { Flow = 4.7 }).Any(pump => pump.Curve == "OH_700"),
    "X 25-200 selection must follow the plotted minimum flow and retain its safety margin.");
var ratingDuty = new PumpRequest(20, 110, Density: 790, Rpm: 2950, Family: "X", XSubtype: "XA", ViscosityCp: 1);
var originalRating = bb2Workbench.Search(ratingDuty).Single(pump => pump.Curve == "OH_716");
var manualRating = new PumpRatingOverride(Efficiency: 45, Npshr: 1.25);
var adjustedRating = PumpWorkbench.BuildReport(new(ratingDuty, originalRating.Index, RatingOverride: manualRating));
double ratedShaftPower = ratingDuty.Density * 9.81 * ratingDuty.Flow * originalRating.Head / (3600000 * .45);
Check(adjustedRating.Pump is { Efficiency: 45, Npshr: 1.25 } &&
    adjustedRating.Pump.Diameter == originalRating.Diameter && adjustedRating.Pump.Head == originalRating.Head &&
    Math.Abs(adjustedRating.Pump.Power - ratedShaftPower) < 1e-9 &&
    adjustedRating.Pump.MotorKw == 15 && adjustedRating.Pump.RatingOverride == manualRating,
    "Manual rating changed the hydraulic selection or did not recalculate shaft power and motor sizing.");
foreach (var (panel, title, expected) in new[] { (0, "Efficiency (%) (manual)", 45d), (1, "NPSHr", 1.25), (2, "Shaft power", ratedShaftPower) })
{
    var curve = adjustedRating.Plots[panel].Series.OfType<LineSeries>().Single(line => line.Title == title);
    Check(curve.Points.Any(point => Math.Abs(point.X - ratingDuty.Flow) < 1e-9 && Math.Abs(point.Y - expected) < 1e-9),
        "The edited curve missed the manual duty point: " + title);
}
var npshOnly = bb2Workbench.GetSelection(ratingDuty, originalRating.Index, new(Npshr: 1.25));
Check(npshOnly.Power == originalRating.Power && npshOnly.Efficiency == originalRating.Efficiency &&
    bb2Workbench.GetSelection(ratingDuty, originalRating.Index) == originalRating,
    "An NPSHr-only adjustment changed power or the unmodified catalog result.");
var npshLimitedDuty = ratingDuty with { Npsha = 2 };
var npshLimitedIndex = bb2Workbench.Search(npshLimitedDuty).Single(pump => pump.Curve == "OH_716").Index;
foreach (var invalid in new[] { new PumpRatingOverride(101), new PumpRatingOverride(0), new PumpRatingOverride(Npshr: -1),
    new PumpRatingOverride(Npshr: 3) })
{
    try
    {
        bb2Workbench.GetSelection(npshLimitedDuty, npshLimitedIndex, invalid);
        throw new InvalidOperationException("An invalid manual rating was accepted.");
    }
    catch (ArgumentException) { }
}
using (var ratedPdf = PumpWorkbench.CreateCurvePdf(new(ratingDuty, originalRating.Index, null, manualRating)))
    Check(ratedPdf.Content.Length > 5000 && ratedPdf.Name.Contains("XA40-315"), "The revised performance PDF did not export.");

var bb2Options = JsonSerializer.SerializeToElement(bb2Workbench.Options(), json);
Check(bb2Options.GetProperty("families").EnumerateArray().Any(f => f.GetString() == "XDB") &&
    bb2Options.GetProperty("families").EnumerateArray().All(f => f.GetString() is not ("HDD" or "KBB")),
    "HDD and KBB must share the XDB selection option.");
var held = JsonSerializer.SerializeToElement(bb2Workbench.Catalog("XDB", heldHddCurve, 1), json);
Check(held.GetProperty("total").GetInt32() == 1 &&
    held.GetProperty("records")[0].GetProperty("id").GetString() == "BB2_LO_49" &&
    !held.GetProperty("records")[0].GetProperty("availableForSelection").GetBoolean(),
    "The partial BB2 sheet is not visible as a review-only curve.");
PumpRequest? bb2Duty = null;
PumpHit? bb2Hit = null;
foreach (var source in bb2.Where(s => s.AvailableForSelection))
{
    double[][] Trace(string quantity, double? diameter = null) => source.Traces.Single(t => t.Quantity == quantity &&
        (diameter == null || t.DiametersMm.SequenceEqual(new[] { diameter.Value }))).Segments.Single();
    double d = source.DiametersMm[^1];
    var head = Trace("head", d);
    var npsh = Trace("npshr");
    var power = Trace("shaft_power", d);
    var efficiency = Trace("efficiency", d);
    var qmin = new[] { head[0][0], npsh[0][0], power[0][0], efficiency[0][0] }.Max();
    var qmax = new[] { head[^1][0], npsh[^1][0], power[^1][0], efficiency[^1][0] }.Min();
    PumpHit? hit = null;
    for (var i = 1; i < 16 && hit == null; i++)
    {
        var flow = qmin + (qmax - qmin) * i / 16;
        var h = PdfTrimCurves.AtFlow(head, flow)!.Value;
        var p = PdfTrimCurves.AtFlow(power, flow)!.Value;
        var e = PdfTrimCurves.AtFlow(efficiency, flow)!.Value;
        var testDuty = new PumpRequest(flow, h, Density: 1100, Rpm: source.Rpm, Family: source.Series, ApiType: "BB2");
        hit = bb2Workbench.Search(testDuty).SingleOrDefault(pump => pump.Curve == source.CurveNumber);
        if (hit == null) continue;
        Check(hit.Series == "XDB" && hit.Family == source.Family && hit.ApiType == "BB2" &&
            hit.OriginalCurve == source.OriginalCurveNumber && hit.Size == source.Size && hit.Stages == source.Stages &&
            hit.Rpm == source.Rpm && Math.Abs(hit.Diameter - d) < 1e-7 && Math.Abs(hit.Power - p * 1.1) < 1e-7 &&
            Math.Abs(hit.Efficiency - e) < 1e-7 && hit.NpshrSource == "pdf-shared" &&
            Math.Abs(hit.Npshr - PdfTrimCurves.AtFlow(npsh, flow)!.Value) < 1e-7,
            $"BB2 curve {source.CurveNumber} did not preserve the source duty point.");
        if (source.CurveNumber == kbbCurve) (bb2Duty, bb2Hit) = (testDuty, hit);
    }
    Check(hit != null, $"BB2 curve {source.CurveNumber} could not be selected at any in-range test duty.");
}
Check(bb2Duty != null && bb2Hit != null, "The published two-stage KBB curve could not be selected.");
var specificBb2Duty = bb2Duty!;
var specificBb2Hit = bb2Hit!;
var bb2Trim = bb2Workbench.Search(new PumpRequest(275, 135, Family: "XDB", ApiType: "BB2"))
    .Single(pump => pump.Curve == kbbCurve);
Check(bb2Trim is { Stages: 2, Rpm: 1475, InterpolatedTrim: true, Diameter: > 437 and < 439,
    Npshr: > 2.1 and < 2.2, Power: > 140 and < 145 } && Math.Abs(bb2Trim.Head - 135) < 1e-7,
    "The two-stage KBB trim did not hit the requested total head.");
bool SelectsKbbCurve(PumpRequest duty) => bb2Workbench.Search(duty).Any(p => p.Curve == kbbCurve);
Check(!SelectsKbbCurve(specificBb2Duty with { Npsha = .1 }) &&
    !SelectsKbbCurve(specificBb2Duty with { ApiType = "OH2" }) &&
    !SelectsKbbCurve(specificBb2Duty with { Rpm = 2950 }) &&
    !SelectsKbbCurve(specificBb2Duty with { ViscosityCp = 50 }),
    "BB2 selection did not enforce NPSH, API type, source speed, or viscosity constraints.");
Check(bb2Workbench.Search(specificBb2Duty with { Stages = 2 }).Any(p => p.Curve == kbbCurve &&
        p.StageLimits == new StageRange(2, 2)) &&
    !SelectsKbbCurve(specificBb2Duty with { Stages = 1 }) &&
    !SelectsKbbCurve(specificBb2Duty with { Clearance = "API" }),
    "A fixed-stage BB2 trace changed its stage count or was used as an API-clearance curve without evidence.");
var bb2Plots = JsonSerializer.SerializeToElement(bb2Workbench.Curves(
    new CurveRequest(specificBb2Duty, specificBb2Hit.Index)), json);
Check(bb2Plots.GetProperty("plots").GetArrayLength() == 3 &&
    bb2Plots.GetProperty("plots")[0].GetString()!.Contains(kbbCurve),
    "The selected BB2 pump did not produce the three performance plots.");
using (var pdf = PumpWorkbench.CreateCurvePdf(new CurvePdfRequest(specificBb2Duty, specificBb2Hit.Index, null)))
    Check(pdf.Content.Length > 5000 && pdf.Name.Contains("XDB380-150x2"), "The selected BB2 pump did not export an XDB PDF.");

var fitModel = new PlotModel();
var fitLine = new LineSeries();
DataPoint[] knots = [new(0, 0), new(4, 9), new(9, 7), DataPoint.Undefined, new(14, 8), new(16, 9), new(17, 12)];
fitLine.Points.AddRange(knots);
fitModel.Series.Add(fitLine);
PlotCurveFit.Apply(fitModel);
Check(fitLine.Points.Count > knots.Length && knots.Where(p => double.IsFinite(p.X)).All(fitLine.Points.Contains) &&
    fitLine.Points.Count(p => double.IsNaN(p.X)) == 1 &&
    !fitLine.Points.Any(p => p.X > 9 && p.X < 14 || p.X <= 9 && (p.Y < 0 || p.Y > 9)),
    "Plot fitting dropped a knot, bridged unavailable data, or introduced a false extremum.");

var digitizedModel = new PlotModel();
var digitized = new LineSeries();
digitized.Points.AddRange(Enumerable.Range(0, 65).Select(q => new DataPoint(q, 1 + q * .02 + (q == 32 ? .3 : 0))));
digitizedModel.Series.Add(digitized);
PlotCurveFit.Apply(digitizedModel, 20.25);
Check(digitized.Points[0] is { X: 0, Y: 1 } && digitized.Points[^1].X == 64 &&
    Math.Abs(digitized.Points[^1].Y - 2.28) < 1e-8 &&
    digitized.Points.Any(p => p.X == 20.25 && Math.Abs(p.Y - 1.405) < 1e-8) &&
    digitized.Points.Where(p => p.X >= 30 && p.X <= 34).Max(p => p.Y) < 1.72,
    "Digitized NPSHr fitting changed the duty or endpoints, or left a raster spike.");

var catalog = JsonSerializer.SerializeToElement(await Call("pumps/catalog"), json);
var ahlstar = AhlstarCatalog.Bundled;
Check(ahlstar.PageCount == 3991 && ahlstar.Records.Length == 3766 &&
    ahlstar.Records.Select(r => r.Page).Distinct().Count() == ahlstar.Records.Length &&
    catalog.GetProperty("total").GetInt32() == 801 + ahlstar.Records.Length - 1 + bb2.Count +
        SmCatalog.Bundled.Records.Length + RegisteredMultistageCatalog.Records.Count,
    "The combined catalog is incomplete.");
var catalogCurves = HydcalcCatalog.Bundled.Curves;
var registered = RegisteredMultistageCatalog.Records;
Check(registered.Count == 71 && registered.Count(row => row.ApiType == "BB3" && row.AvailableForSelection) == 19 &&
    registered.Count(row => row.ApiType == "BB4" && row.AvailableForSelection) == 40,
    "The register or engineering review flags were lost.");
foreach (var (api, start, count) in new[] { ("BB3", 448, 31), ("BB4", 414, 40) })
{
    var codes = registered.Where(row => row.ApiType == api).Select(row => int.Parse(row.CurveNumber.Split('_')[^1]));
    Check(codes.SequenceEqual(Enumerable.Range(start, count)), api + " numbering did not continue in register order.");
}
var correctedRegisterRow = registered.Single(row => row.CurveNumber == "BB3_LO_SCSK_125-75_454");
Check(correctedRegisterRow.Size == "125-75" && correctedRegisterRow.RegisterCode == "BB3_LO_SCSK_125-7" &&
    correctedRegisterRow.StageLimits.SequenceEqual(new[] { 3, 12 }) &&
    correctedRegisterRow.ReferenceStages == null && !correctedRegisterRow.AvailableForSelection,
    "The confirmed correction lost provenance or bypassed the stage-basis blocker.");
Check(registered.Where(row => row.ApiType == "BB3").All(row => row.Size == row.SourceSize),
    "The confirmed BB3 size corrections still disagree with the drawings.");
foreach (var (code, size, selectable) in new[]
{
    ("BB3_LO_SCSK_520-120_472", "520-120", false), ("BB3_LO_SCSK_1000-220_477", "1000-220", false),
    ("BB3_LO_SCSK_1200-200_478", "1200-200", true)
})
{
    var correctedSizeRow = registered.Single(row => row.CurveNumber == code);
    Check(correctedSizeRow.Size == size && correctedSizeRow.AvailableForSelection == selectable,
        "The corrected BB3 row has an incorrect size or review status: " + code);
}
var correctedLibrary = JsonSerializer.SerializeToElement(bb2Workbench.Catalog("XDA", correctedRegisterRow.CurveNumber, 1), json);
Check(correctedLibrary.GetProperty("records")[0].GetProperty("issues").EnumerateArray()
        .Any(issue => issue.GetString()!.Contains("3-12", StringComparison.Ordinal)),
    "The confirmed operating stage limits are not visible in the curve library.");
foreach (var series in new[] { "XMC", "XMD", "XMR" })
{
    var list = JsonSerializer.SerializeToElement(bb2Workbench.Catalog(series, "BB4_LO_", 1), json);
    Check(list.GetProperty("total").GetInt32() == 40 &&
        list.GetProperty("records")[0].GetProperty("series").GetString() == "XMC / XMD / XMR",
        "The applicable series filters did not expose the same 40 drawings.");
}
var nativeXmr = JsonSerializer.SerializeToElement(bb2Workbench.Catalog("XMR", "BB5_", 1), json);
Check(nativeXmr.GetProperty("total").GetInt32() > 0, "The native BB5 XMR records were lost.");
foreach (var record in registered.Where(row => row.AvailableForSelection))
{
    var diameter = record.DiametersMm[^1];
    double[][] Trace(string quantity) => record.Traces.Single(t => t.Quantity == quantity &&
        t.DiametersMm.SequenceEqual(new[] { diameter })).Segments.Single();
    var head = Trace("head");
    var npsh = Trace("npshr");
    var power = Trace("shaft_power");
    double min = new[] { head[0][0], npsh[0][0], power[0][0], record.MinimumFlowM3h ?? 0 }.Max();
    double max = new[] { head[^1][0], npsh[^1][0], power[^1][0] }.Min();
    var stages = record.StageLimits[0];
    bool matched = false;
    for (int i = 1; i < 16 && !matched; i++)
    {
        var flow = min + (max - min) * i / 16;
        var dutyAtDrawing = new HydcalcDuty { Flow = flow, Head = PdfTrimCurves.AtFlow(head, flow)!.Value * stages,
            FixedRpm = record.Rpm, Density = 1100, Stages = stages };
        var hits = RegisteredMultistageCatalog.Search(dutyAtDrawing, null, record.ApiType)
            .Where(hit => hit.HydraulicLabel == record.CurveNumber).ToArray();
        if (hits.Length == 0) continue;
        matched = true;
        Check(hits.Select(hit => hit.Curve.Series).SequenceEqual(record.PumpTypes) && hits.All(hit =>
            hit.StageLimits == new StageRange(stages, record.StageLimits[1]) && hit.DiameterRatio == 1 &&
            Math.Abs(hit.Head - dutyAtDrawing.Head) < 1e-7 &&
            Math.Abs(hit.Npshr - PdfTrimCurves.AtFlow(npsh, flow)!.Value) < 1e-7 &&
            Math.Abs(hit.PowerKw - stages * 1.1 * PdfTrimCurves.AtFlow(power, flow)!.Value) < 1e-7),
            "Registered stage or hydraulic data did not reproduce " + record.CurveNumber);
        if (record.ApiType == "BB4")
        {
            var shared = RegisteredMultistageCatalog.Search(dutyAtDrawing, "XMR", "BB5")
                .Single(hit => hit.HydraulicLabel == record.CurveNumber);
            var baseline = hits[0];
            Check(shared.Curve.Series == "XMR" && shared.PdfReference.SharedHydraulicApiType == "BB4" &&
                shared.OriginalHydraulicLabel == record.OriginalCurveNumber && shared.StageLimits == baseline.StageLimits &&
                shared.DiameterRatio == baseline.DiameterRatio && shared.Head == baseline.Head &&
                shared.Npshr == baseline.Npshr && shared.Efficiency == baseline.Efficiency && shared.PowerKw == baseline.PowerKw,
                "The XMR calculation did not match the BB4 source for " + record.CurveNumber);
        }
    }
    Check(matched, "No in-range test duty matched " + record.CurveNumber);
}
const string bb4Code = "BB4_LO_M15-35_GSG_415";
var registeredDuty = new PumpRequest(22.674876, 102.444, Rpm: 2980, Family: "XMD", ApiType: "BB4", Stages: 3);
var registeredHit = ((PumpHit[])await Call("pumps/search", registeredDuty)).Single(hit => hit.Curve == bb4Code);
Check(registeredHit is { Series: "XMD", Diameter: 175, Stages: 3, Rpm: 2980 } &&
    registeredHit.StageLimits == new StageRange(3, 15) &&
    bb2Workbench.Search(registeredDuty with { Family = "XMC" }).Any(hit => hit.Curve == bb4Code && hit.Series == "XMC"),
    "The shared BB4 trace is not available under both model series.");
foreach (var excluded in new[] { registeredDuty with { Stages = 2, Head = 68.2958 },
    registeredDuty with { Rpm = 2950 }, registeredDuty with { Head = 95 }, registeredDuty with { Npsha = 0.5 },
    registeredDuty with { Clearance = "API" } })
    Check(!bb2Workbench.Search(excluded).Any(hit => hit.Curve == bb4Code), "A BB4 search exceeded the published basis.");
var registeredPlots = JsonSerializer.SerializeToElement(await Call("pumps/curves",
    new { duty = registeredDuty, index = registeredHit.Index }), json);
Check(registeredPlots.GetProperty("plots")[0].GetString()!.Contains("max impeller only") &&
    registeredPlots.GetProperty("report").GetProperty("mcsfFlow").GetDouble() == 6.5,
    "The BB4 plot/report omitted the published minimum flow or trim restriction.");
using (var registeredPdf = (WorkbenchDownload)await Call("pumps/export-pdf",
    new { duty = registeredDuty, index = registeredHit.Index }, save: true))
    Check(registeredPdf.Name.Contains("XMDM15-35_GSG") && registeredPdf.Content.Length > 5000,
        "The registered BB4 curve did not export under its selected alias.");
var sharedDuty = registeredDuty with { Family = "XMR", ApiType = "BB5" };
var sharedHit = ((PumpHit[])await Call("pumps/search", sharedDuty)).Single(hit => hit.Curve == bb4Code);
Check(sharedHit is { Series: "XMR", ApiType: "BB5", SharedHydraulicApiType: "BB4", Suction: "Single" } &&
    sharedHit.Diameter == registeredHit.Diameter && sharedHit.OriginalCurve == registeredHit.OriginalCurve &&
    sharedHit.MotorKw == registeredHit.MotorKw,
    "The XMR workbench result did not preserve the source and classify it under BB5.");
foreach (var excluded in new[] { sharedDuty with { ApiType = "BB4" }, sharedDuty with { Stages = 2 },
    sharedDuty with { Rpm = 2950 }, sharedDuty with { Npsha = 0.5 }, sharedDuty with { Head = 95 },
    sharedDuty with { Clearance = "API" }, sharedDuty with { ViscosityCp = 8 } })
    Check(!bb2Workbench.Search(excluded).Any(hit => hit.Curve == bb4Code), "XMR accepted a duty outside the shared curve's basis.");
var sharedPlots = JsonSerializer.SerializeToElement(await Call("pumps/curves",
    new { duty = sharedDuty, index = sharedHit.Index }), json);
Check(sharedPlots.GetProperty("plots")[0].GetString()!.Contains("Shared BB4 hydraulics") &&
    sharedPlots.GetProperty("plots")[1].GetString()!.Contains("Shared hydraulic trace") &&
    sharedPlots.GetProperty("report").GetProperty("mcsfFlow").GetDouble() == 6.5,
    "The shared hydraulic basis or published MCSF was omitted from the report.");
using (var sharedPdf = (WorkbenchDownload)await Call("pumps/export-pdf",
    new { duty = sharedDuty, index = sharedHit.Index }, save: true))
    Check(sharedPdf.Name.Contains("XMRM15-35_GSG") && sharedPdf.Content.Length > 5000,
        "The shared LO curve did not export under the XMR model.");

var sm = SmCatalog.Bundled;
Check(sm.Records.Length == 57 && sm.Records.Count(record => record.AvailableForSelection) == 9 &&
    sm.Records.Select(record => record.SourcePage).SequenceEqual(Enumerable.Range(2, 57)) &&
    sm.Records.All(record => record.CurveNumber == $"SM_{record.Size}_{record.SourcePage+617}"),
    "The SM page index, trace status or XDC numbering changed.");
var smRows = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=XDC&query=SM_"), json);
Check(smRows.GetProperty("total").GetInt32() == 57 &&
    smRows.GetProperty("records")[0].GetProperty("originalCurveNumber").GetString() == "D 1600.01.01/1",
    "The XDC library did not preserve the new and original SM numbers.");
var smPending = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=SM&query=SM_152-320_622"), json);
Check(smPending.GetProperty("total").GetInt32() == 1 &&
    !smPending.GetProperty("records")[0].GetProperty("availableForSelection").GetBoolean(),
    "A header-only SM record is not marked for review.");
foreach (var record in sm.Records.Where(record => record.AvailableForSelection))
{
    var impeller = record.Impellers[^1];
    PumpHit? hit = null;
    for (int i = 1; i <= 15 && hit == null; i++)
    {
        var flow = impeller.Npshr[0][0] + i / 16d * (impeller.Npshr[^1][0] - impeller.Npshr[0][0]);
        if (PdfTrimCurves.AtFlow(impeller.Head, flow) is not double head ||
            PdfTrimCurves.AtFlow(impeller.Power, flow) is not double power) continue;
        hit = bb2Workbench.Search(new PumpRequest(flow, head, Rpm: record.Rpm, Family: "XDC", ApiType: "BB1"))
            .SingleOrDefault(p => p.Curve == record.CurveNumber);
        if (hit != null)
            Check(hit is { Family: "SM", Series: "XDC", ApiType: "BB1", Stages: 1, Suction: "Double" } &&
                hit.StageLimits == new StageRange(1, 1) && hit.OriginalCurve == record.OriginalCurveNumber &&
                hit.Rpm == record.Rpm && Math.Abs(hit.Head - head) < 1e-7 && Math.Abs(hit.Power - power) < 1e-7 &&
                Math.Abs(hit.Npshr - PdfTrimCurves.AtFlow(impeller.Npshr, flow)!.Value) < 1e-7,
                $"SM {record.CurveNumber} did not reproduce the metric trace.");
    }
    Check(hit != null, $"No in-range test duty matched SM {record.CurveNumber}.");
}
var smDuty = new PumpRequest(200, 75, Rpm: 2950, Family: "XDC", ApiType: "BB1");
var smHit = ((PumpHit[])await Call("pumps/search", smDuty)).Single(p => p.Curve == "SM_126-250_619");
Check(smHit.Diameter is > 240 and < 250 && smHit.Npshr is > 3 and < 8 && smHit.NpshrSource == "interpolated" &&
    bb2Workbench.Search(smDuty with { Family = "SM", Rpm = 2980 }).Length == 0 &&
    bb2Workbench.Search(smDuty with { Family = "SM", Stages = 2 }).Length == 0 &&
    bb2Workbench.Search(smDuty with { Family = "SM", Clearance = "API" }).Length == 0 &&
    bb2Workbench.Search(smDuty with { Family = "SM", Npsha = 0.5 }).Length == 0,
    "SM trim, NPSH coverage, stage, speed or unknown-clearance filters are incorrect.");
var smPlots = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = smDuty, index = smHit.Index }), json);
Check(smPlots.GetProperty("plots").GetArrayLength() == 3 &&
    smPlots.GetProperty("plots")[0].GetString()!.Contains(smHit.Curve), "SM plots did not use the new curve number.");
using (var smPdf = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = smDuty, index = smHit.Index }, save: true))
    Check(smPdf.Content.Length > 5000 && smPdf.Name.Contains("XDC126-250"), "The SM curve did not export under XDC.");
Check(catalogCurves.All(curve => (curve.CurveNumber != curve.Name || curve.Name == "K50551") &&
        curve.OriginalCurveNumber != curve.Name) &&
    catalogCurves.Select(curve => curve.CurveNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 801,
    "A curve is missing a unique new number or its original number.");
var options = JsonSerializer.SerializeToElement(await Call("pumps/options"), json);
Check(options.GetProperty("families").EnumerateArray().Select(value => value.GetString()).SequenceEqual(
    new[] { "A", "APP", "EPP", "MSD", "MSD2", "NPP", "OHH", "WPP", "X", "XC", "XDA", "XDB", "XDC", "XMB", "XMC", "XMD", "XMR", "XTT" }),
    "Selection filters are not using the current pump series.");
var ahlstarCurve = catalogCurves.Single(curve => curve.Name == "K50551");
var mbDuty = new PumpRequest(30, 50, Rpm: 1480, Family: "XMB", ApiType: "BB4");
var mbStandard = (PumpHit[])await Call("pumps/search", mbDuty with { Clearance = "Standard" });
var mbApi = (PumpHit[])await Call("pumps/search", mbDuty with { Clearance = "API" });
Check(mbStandard.Length > 0 && mbStandard.All(p => p.Clearance == "Standard") &&
    mbApi.Length > 0 && mbApi.All(p => p.Clearance == "API") &&
    mbStandard.Any(p => p.Size == "50-180" && p.OriginalCurve.Contains("4243.11.11/1") &&
        p.StageLimits == new StageRange(2, 15)) &&
    mbApi.Any(p => p.Size == "50-180" && p.OriginalCurve.Contains("4243.11.51/01")),
    "MB 50-180 did not use the linked source hydraulic for the requested clearance.");
var mbCurve = catalogCurves.Single(c => c.Name == "4243.11.11/1");
var mbBep = HydcalcSizing.Bep(mbCurve);
foreach (var (rpm, maximumStages) in new[] { (2950, 15), (3300, 10) })
{
    var flow = mbBep.FlowM3h * rpm / mbCurve.Rpm!.Value;
    var head = mbBep.Head * maximumStages * Math.Pow(rpm / mbCurve.Rpm.Value, 2);
    var stageDuty = new PumpRequest(flow, head, Rpm: rpm, Family: "XMB", Stages: maximumStages, Clearance: "Standard");
    Check(bb2Workbench.Search(stageDuty).Any(p => p.Size == "50-180" && p.StageLimits == new StageRange(2, maximumStages) &&
            p.Stages == maximumStages) &&
        !bb2Workbench.Search(stageDuty with { Stages = maximumStages + 1 }).Any(p => p.Size == "50-180") &&
        !bb2Workbench.Search(stageDuty with { Stages = 1 }).Any(p => p.Family == "MB"),
        $"MB 50-180 did not enforce the stage range at {rpm} rpm.");
}
foreach (var invalid in new[] { mbDuty with { Clearance = "Hot" }, mbDuty with { Clearance = "Other" }, mbDuty with { Stages = 0 } })
{
    try { await Call("pumps/search", invalid); throw new InvalidOperationException("An unavailable clearance or invalid stage count was accepted."); }
    catch (ArgumentException e) { Check(e.Message.Contains(invalid.Clearance == "Hot" ? "Hot" : invalid.Clearance == null ? "级数" : "间隙"), "Selection did not explain the invalid filter."); }
}
var msdLibrary = JsonSerializer.SerializeToElement(bb2Workbench.Catalog("MSDD", "MD-188.0", 1), json);
Check(msdLibrary.GetProperty("total").GetInt32() == 1 &&
    msdLibrary.GetProperty("records")[0].GetProperty("series").GetString() == "MSD2",
    "The original MSD-DD curve was not retained under MSD2.");
foreach (var (family, size, series, suction, stages) in new[] {
    ("MSD", "14x16x26A", "MSD", "Single", 2), ("MSD-D", "10x10x14.5A", "MSD2", "Double", 3) })
{
    var source = catalogCurves.Single(c => c.Family == family && c.Size == size);
    var bep = HydcalcSizing.Bep(source);
    var selected = new PumpRequest(bep.FlowM3h, bep.Head * stages, Rpm: (int)Math.Round(source.Rpm!.Value),
        Family: series, ApiType: "BB3", Stages: stages, Clearance: "API");
    var hit = bb2Workbench.Search(selected).FirstOrDefault(p => p.Size == size && p.Curve == source.CurveNumber);
    Check(hit != null && hit.ModelSeries == series && hit.Suction == suction &&
        !bb2Workbench.Search(selected with { Family = series == "MSD" ? "MSD2" : "MSD" }).Any(p => p.Curve == source.CurveNumber),
        $"The {family} source curve was not isolated under {series}.");
    if (series == "MSD2")
    {
        using var pdf = PumpWorkbench.CreateCurvePdf(new CurvePdfRequest(selected, hit!.Index, null));
        Check(pdf.Name.Contains("MSD2" + size) && pdf.Content.Length > 5000, "The double-suction PDF did not use MSD2.");
    }
}
Check(ahlstarCurve.Family == "A" && ahlstarCurve.Size == "23-40" && ahlstarCurve.Points.Count == 40 &&
    ahlstarCurve.Issues.Count == 0 && HydcalcCatalog.Bundled.LinkedProperties(ahlstarCurve).Single().Field(0) == "A",
    "The A23-40 curve or hydraulic link is incomplete.");
var aDuty = new PumpRequest(30, 150, Family: "A");
foreach (var (family, curve, size, rpm, flow, head) in new[]
{
    ("APP", "K14130", "53-200", 985, 450d, 35d),
    ("A", "K18490", "33-100", 1470, 144d, 50d),
    ("APP", "K13847", "33-100", 1470, 144d, 45d),
    ("EPP", "K15296", "52-350", 1190, 1600d, 30d),
    ("NPP", "K15413", "31-100", 1770, 200d, 20d),
    ("WPP", "K15587", "32-80", 860, 60d, 10d),
    ("A", "K61117", "71-300", 740, 1729.7145204, 26.4343742),
    ("APP", "K14131", "53-250", 980, 653.3678402, 27.4266908),
    ("EPP", "K15299", "53-200", 1180, 518.8260708, 43.1931465),
    ("NPP", "K15414", "32-80", 1770, 161.0763753, 30.8543089),
    ("WPP", "K15588", "32-100", 860, 81.2031069, 7.2313368),
    ("A", "K18649", "10-32", 2905, 10.9044531, 15.8974026)
})
{
    var importDuty = new PumpRequest(flow, head, Rpm: rpm, Family: family);
    var hits = (PumpHit[])await Call("pumps/search", importDuty);
    var hit = hits.Single(p => p.Curve == curve);
    Check(hit is { InterpolatedTrim: true, Efficiency: > 0 and < 100, Npshr: > 0, Power: > 0 } &&
        hit.Curve == curve && hit.OriginalCurve == curve && hit.Series == family && hit.Size == size &&
        hit.Rpm == rpm && hit.SourcePage is > 0 && Math.Abs(hit.Head - head) < 1e-7,
        "The verified AHLSTAR path did not work for " + family);
    var chart = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = importDuty, index = hit.Index }), json);
    Check(chart.GetProperty("printPlots")[0].GetString()!.Contains(curve), "The original AHLSTAR curve number is missing from the plot.");
    using var pdf = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = importDuty, index = hit.Index }, save: true);
    Check(IsPdf(pdf) && pdf.Name == $"Curve_{family}{size}_{rpm}RPM_V0.pdf", "The AHLSTAR report did not preserve the pump size.");
    Check(((PumpHit[])await Call("pumps/search", importDuty with { Rpm = rpm + 10 })).All(p => p.Curve != curve),
        "A bulk import incorrectly allowed an unpublished speed.");
}
Check(AhlstarCatalog.VerifiedTraces.Count > 750 &&
    AhlstarCatalog.VerifiedTraces.Select(curve => curve.Page).Distinct().Count() == AhlstarCatalog.VerifiedTraces.Count &&
    AhlstarCatalog.VerifiedTraces.All(curve => curve.Impellers.Count(i => i.Npshr.Length > 0) >= 2),
    "The calibrated bulk AHLSTAR traces are missing or have invalid source-page links.");
var a33 = AhlstarCatalog.VerifiedTraces.Single(curve => curve.CurveNumber == "K18490");
var app33 = AhlstarCatalog.VerifiedTraces.Single(curve => curve.CurveNumber == "K13847");
Check(a33.Page == 56 && app33.Page == 1066 &&
    a33.Impellers.Select(i => i.DiameterMm).SequenceEqual(new double[] { 400, 380, 360, 340, 320 }) &&
    app33.Impellers.Select(i => i.DiameterMm).SequenceEqual(new double[] { 400, 370, 340, 330 }) &&
    a33.Impellers.Where(i => i.Npshr.Length > 0).Select(i => i.DiameterMm).SequenceEqual(new double[] { 400, 360, 320 }) &&
    app33.Impellers.Where(i => i.Npshr.Length > 0 && !i.NpshrAffinitySourceDiameterMm.HasValue)
        .Select(i => i.DiameterMm).SequenceEqual(new double[] { 400, 370, 340 }),
    "The reviewed A33-100 and APP33-100 traces lost their published diameter coverage.");
var a33Duty = new PumpRequest(144, 50, Rpm: 1470, Family: "A");
var app33Duty = new PumpRequest(144, 45, Rpm: 1470, Family: "APP");
var a33Hit = ((PumpHit[])await Call("pumps/search", a33Duty)).Single(pump => pump.Curve == "K18490");
var app33Hit = ((PumpHit[])await Call("pumps/search", app33Duty)).Single(pump => pump.Curve == "K13847");
Check(a33Hit is { Diameter: > 369.8 and < 370.2, Npshr: > 1.47 and < 1.52, NpshrSource: "interpolated" } &&
    app33Hit is { Diameter: > 385.8 and < 386.4, Npshr: > 1.63 and < 1.67, NpshrSource: "interpolated" },
    "The 33-100 rated samples disagree with their source axes or impeller traces.");
var app330 = app33.Impellers.Single(i => i.DiameterMm == 330);
var app340 = app33.Impellers.Single(i => i.DiameterMm == 340);
const double ratio330 = 330d / 340;
var app330Duty = app33Duty with { Head = PdfTrimCurves.AtFlow(app330.Head, 144)!.Value };
var app330Hit = ((PumpHit[])await Call("pumps/search", app330Duty)).Single(pump => pump.Curve == "K13847");
double affinity = Math.Max(PdfTrimCurves.AtFlow(app340.Npshr, 144)!.Value,
    PdfTrimCurves.AtFlow(app340.Npshr, 144 / ratio330)!.Value * ratio330 * ratio330);
Check(app330.NpshrAffinitySourceDiameterMm == 340 && app330Hit.Diameter == 330 &&
    Math.Abs(app330Hit.Npshr - affinity) < .002 && app330Hit.NpshrSource == PdfTrimCurves.AffinityEstimatedNpshr &&
    Math.Abs(app330Hit.Power - PdfTrimCurves.AtFlow(app330.Power, 144)!.Value) < 1e-7,
    "APP33-100 330 mm must retain published power and identify its estimated NPSHr.");
var app330Chart = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = app330Duty, index = app330Hit.Index }), json);
Check(app330Chart.GetProperty("printPlots")[1].GetString()!.Contains("Affinity estimate"),
    "The report curve did not warn about estimated NPSHr.");
using (var app330Pdf = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = app330Duty, index = app330Hit.Index }, save: true))
    Check(IsPdf(app330Pdf) && app330Pdf.Name == "Curve_APP33-100_1470RPM_V0.pdf", "The estimated APP trim did not export.");

var sourceShape = new HydcalcCurve();
int available33100Trims = 0;
foreach (var (trimDuty33, curve33) in new[] { (a33Duty, a33), (app33Duty, app33) })
{
    var catalog33 = AhlstarCatalog.Bundled.Records.Single(row => row.Page == curve33.Page);
    foreach (double diameter in catalog33.TrimDiametersMm)
    {
        var sample = curve33.Sample(sourceShape, trimDuty33.Rpm!.Value, diameter, trimDuty33.Flow);
        var hits = (PumpHit[])await Call("pumps/search", trimDuty33 with { Head = sample.Head!.Value });
        string? expectedSource = curve33 == app33 && diameter < 340 ? PdfTrimCurves.AffinityEstimatedNpshr : sample.NpshrSource;
        Check(hits.Any(pump => pump.Curve == curve33.CurveNumber && Math.Abs(pump.Diameter - diameter) < 1e-6 &&
            pump.NpshrSource == expectedSource), $"{catalog33.Family} 33-100 {diameter} mm cannot be selected.");
        available33100Trims++;
    }
}
Check(available33100Trims == 48, "The 33-100 selection check must cover every listed trim.");
Check(!((PumpHit[])await Call("pumps/search", app330Duty with { Flow = 30, Head = PdfTrimCurves.AtFlow(app330.Head, 30)!.Value }))
        .Any(pump => pump.Curve == "K13847") &&
    !((PumpHit[])await Call("pumps/search", app330Duty with { Npsha = 2.4 })).Any(pump => pump.Curve == "K13847") &&
    !((PumpHit[])await Call("pumps/search", a33Duty with { Npsha = 1.1 })).Any(pump => pump.Curve == "K18490") &&
    !((PumpHit[])await Call("pumps/search", a33Duty with { Family = "APP" })).Any(pump => pump.Curve == "K18490") &&
    !((PumpHit[])await Call("pumps/search", app33Duty with { Family = "A" })).Any(pump => pump.Curve == "K13847"),
    "A 33-100 curve was used outside its verified NPSHr data or under the wrong family.");
var bulkRows = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=A&query=K61117"), json);
Check(bulkRows.GetProperty("records").EnumerateArray().Any(row =>
    row.GetProperty("availableForSelection").GetBoolean() &&
    row.GetProperty("source").GetProperty("page").GetInt32() == 321 &&
    row.GetProperty("tracedDiametersMm").GetArrayLength() == 5),
    "The three-panel A71-300 legacy curve was not linked to the original sheet.");
var scanned = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=A&query=K18649"), json);
Check(scanned.GetProperty("records").EnumerateArray().Any(row =>
    row.GetProperty("availableForSelection").GetBoolean() && row.GetProperty("points").GetInt32() > 200 &&
    row.GetProperty("source").GetProperty("page").GetInt32() == 5 &&
    row.GetProperty("tracedDiametersMm").GetArrayLength() == 5),
    "The OCR chart was not linked to its original sheet and five extracted impellers.");
var documentOnly = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=APP&query=K14401"), json);
var documented = documentOnly.GetProperty("records").EnumerateArray().First();
Check(!documented.GetProperty("availableForSelection").GetBoolean() &&
    documented.GetProperty("source").GetProperty("minRpm").GetInt32() == 600 &&
    documented.GetProperty("source").GetProperty("maxRpm").GetInt32() == 1500,
    "An unreviewed variable-speed chart lost its source range or was enabled for selection.");
var aSelection = ((PumpHit[])await Call("pumps/search", aDuty)).Single();
Check(aSelection is { Curve: "K50551", Rpm: 2980, SourceRpm: 2950, Diameter: > 308.78 and < 308.79,
    MinDiameter: 210, MaxDiameter: 330, Efficiency: > 33.98 and < 34, Power: > 36.07 and < 36.08, MotorKw: 45,
    Npshr: > 1.55 and < 1.60, InterpolatedTrim: true, NpshrSource: "interpolated-with-reference" } &&
    Math.Abs(aSelection.Head - 150) < 1e-8, "A23-40 did not solve the duty on the digitized PDF curves.");
foreach (var noMatch in new[] { aDuty with { Rpm = 2950 }, aDuty with { Head = 175 },
    aDuty with { Flow = 90 }, aDuty with { Poles = 4 }, aDuty with { Npsha = 1 },
    aDuty with { ViscosityCp = 20 } })
    Check(((PumpHit[])await Call("pumps/search", noMatch)).Length == 0,
        "A23-40 must respect the stored speed, trace limits, NPSH margin and applicable liquid conditions.");
var aPlots = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = aDuty, index = 0 }), json);
var aHeadPlot = aPlots.GetProperty("printPlots")[0].GetString()!;
Check(aHeadPlot.Contains("K50551") && aHeadPlot.Contains("Rated D=308.8 mm") &&
    aHeadPlot.Contains("Minimum D=210 mm") && aHeadPlot.Contains("Maximum D=330 mm") &&
    aHeadPlot.Contains("interpolated trim") &&
    aPlots.GetProperty("printPlots")[1].GetString()!.Contains("Estimated; verify with manufacturer") &&
    aPlots.GetProperty("report").GetProperty("mcsfFlow").ValueKind == JsonValueKind.Null &&
    aPlots.GetProperty("report").GetProperty("shutoffHead").ValueKind == JsonValueKind.Null,
    "A23-40 must identify the estimated data and omit unverified shutoff and MCSF values.");
using (var aPdf = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = aDuty, index = 0 }, save: true))
    Check(IsPdf(aPdf) && aPdf.Name == "Curve_A23-40_2980RPM_V0.pdf", "A23-40 curve PDF export failed.");
var screenshotDuty = new PumpRequest(42, 150, Rpm: 2980, Family: "A");
var screenshot = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = screenshotDuty, index = 0 }), json);
Check(Math.Abs(screenshot.GetProperty("pump").GetProperty("npshr").GetDouble() - 1.87303648) < 1e-7 &&
    Math.Abs(screenshot.GetProperty("report").GetProperty("nss").GetDouble() - 9991.823796) < 1e-5,
    "Smoothing changed hydraulic selection or report values at 42 m3/h and 150 m.");
var trimDuty = new PumpRequest(30, 150, Rpm: 2980, Family: "A");
var trim = ((PumpTrimPreview[])await Call("pumps/trim-preview", trimDuty)).Single();
Check(trim.Curve == "K50551" && trim.Rpm == 2980 && trim.SourceRpm == 2950 && trim.SourcePage == 21 &&
    trim.RequestedHead == 150 && trim.Plots.Length == 3 &&
    trim.Points.Select(point => point.Diameter).SequenceEqual(new[] { 330d, 315d, 280d, 245d, 210d }),
    "The A23-40 trim preview does not show the five listed diameters.");
Check(trim.Points[0] is { Head: > 174.42 and < 174.44, NpshrSource: "supplied-reference" } &&
    trim.Points[1] is { Head: > 156.66 and < 156.68, Efficiency: > 33.65 and < 33.68,
        Npshr: > 1.55 and < 1.60, NpshrSource: "interpolated-with-reference" } &&
    trim.Points[2] is { Head: > 119.10 and < 119.12, NpshrSource: "pdf" } &&
    trim.Points[3] is { Head: > 86.97 and < 87.00, NpshrSource: "interpolated" } &&
    trim.Points[4] is { Head: > 58.83 and < 58.86, NpshrSource: "pdf" },
    "PDF-based trim comparison data or source labels are wrong.");
Check(trim.Points.All(point => point.Samples.Count(sample => sample.HeadM.HasValue) >= 40 &&
    point.Samples.Any(sample => sample.EfficiencyPercent.HasValue) &&
    point.Samples.Any(sample => sample.NpshrM.HasValue)), "The comparison did not create complete trace datasets.");
var sourceSpeed = ((PumpTrimPreview[])await Call("pumps/trim-preview", trimDuty with { Rpm = 2950 })).Single();
Check(sourceSpeed.Points[1].Head is > 153.36 and < 153.39, "The 2950 rpm preview did not retain the PDF head.");
var reflow = ((PumpTrimPreview[])await Call("pumps/trim-preview", trimDuty with { Flow = 70 })).Single();
Check(reflow.Points.Take(2).All(point => point.Head > 0) && reflow.Points.Skip(2).All(point => point.Head == null),
    "The trim comparison extrapolated outside the scaled curve flow range.");
var exact = ((PumpHit[])await Call("pumps/search", trimDuty with { Head = trim.Points[1].Head!.Value })).Single();
Check(Math.Abs(exact.Diameter - 315) < 1e-8 && !exact.InterpolatedTrim,
    "An exact published diameter was not distinguished from a trim estimate.");
foreach (var noPreview in new[] { trimDuty with { Rpm = 2900 }, trimDuty with { ViscosityCp = 20 } })
    Check(((PumpTrimPreview[])await Call("pumps/trim-preview", noPreview)).Length == 0,
        "A head estimate was shown at an unsupported speed or for viscous duty.");
using (var file = (WorkbenchDownload)await Call("pumps/export-trim-data", trimDuty, save: true))
using (var data = await JsonDocument.ParseAsync(file.Content))
    Check(file.Name == "Impeller_Trim_Curves.json" && data.RootElement.GetProperty("curves")[0]
        .GetProperty("impellers")[4].GetProperty("samples").GetArrayLength() >= 40,
        "Download is missing the digitized impeller curves.");
var xSubtypes = new[] { "XA", "XE", "XF", "XZX", "XGP" };
Check(options.GetProperty("xSubtypes").EnumerateArray().Select(value => value.GetString()).SequenceEqual(xSubtypes),
    "The X subtype list is incomplete.");
var xDuty = new PumpRequest(35, 55, Family: "X");
var genericX = (PumpHit[])await Call("pumps/search", xDuty);
var xIndex = genericX.First(hit => hit.Size == "50-200").Index;
foreach (var subtype in xSubtypes)
{
    var variant = (PumpHit[])await Call("pumps/search", xDuty with { XSubtype = subtype });
    Check(variant.All(hit => hit.ModelSeries == subtype && hit.Series == "X" && hit.Family == "Z") &&
        variant.Select(hit => hit with { XSubtype = null }).SequenceEqual(genericX),
        subtype + " must change only the model label, not the selection or performance.");
}
foreach (var invalid in new[] { xDuty with { XSubtype = "unknown" },
    xDuty with { Family = "XC", XSubtype = "XA" }, xDuty with { Family = null, XSubtype = "XA" } })
{
    try
    {
        await Call("pumps/search", invalid);
        throw new InvalidOperationException("An invalid X subtype selection was accepted.");
    }
    catch (ArgumentException) { }
}
var variantDuty = xDuty with { XSubtype = " xgp " };
var variantCurves = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = variantDuty, index = xIndex }), json);
Check(variantCurves.GetProperty("printPlots")[0].GetString()!.Contains("XGP 50-200"),
    "The printable plot omitted the X subtype.");
using (var variantPdf = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = variantDuty, index = xIndex }, save: true))
    Check(IsPdf(variantPdf) && variantPdf.Name == "Curve_XGP50-200_2950RPM_V0.pdf", "The curve PDF filename omitted the X subtype.");
Check(((string[])await Submit("pump-models", new { duty = variantDuty, index = xIndex })).Contains("ZE50-2200"),
    "An X subtype must use the original ZTools hydraulic-model mapping.");
var unavailable = new Dictionary<string, int> { ["BKC"] = 42, ["CAP8"] = 71, ["HZZ"] = 14, ["MSE"] = 7 };
Check(catalogCurves.Count(curve => !curve.AvailableForSelection) == 134 &&
    catalogCurves.Sum(curve => curve.Points.Count) == 32040, "The retained catalog or its point count changed.");
foreach (var (family, count) in unavailable)
{
    var retained = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=" + family), json);
    Check(retained.GetProperty("total").GetInt32() == count &&
        retained.GetProperty("records").EnumerateArray().All(record =>
            record.GetProperty("series").GetString() == family && !record.GetProperty("availableForSelection").GetBoolean()),
        family + " must remain in the library under its original name only.");
    Check(((PumpHit[])await Call("pumps/search", new PumpRequest(35, 55, Family: family))).Length == 0,
        family + " must not be available for selection.");
}
var grouped = JsonSerializer.SerializeToElement(await Call("pumps/catalog?family=XDB"), json);
Check(grouped.GetProperty("total").GetInt32() == 204, "The XDB series must include the 51 HDD/KBB drawings.");
foreach (var query in new[] { "bb2_1", "BBT_2x4x13-1_B_BBT 01-1", "BBT 01-1" })
{
    var matches = JsonSerializer.SerializeToElement(await Call("pumps/catalog?query=" + Uri.EscapeDataString(query)), json);
    Check(matches.GetProperty("records").EnumerateArray().Any(record =>
        record.GetProperty("id").GetString() == "1" &&
        record.GetProperty("curveNumber").GetString() == "BB2_1" &&
        record.GetProperty("originalCurveNumber").GetString() == "BBT_2x4x13-1_B_BBT 01-1"),
        "Catalog did not find the original curve using " + query);
}

var duty = new PumpRequest(35, 55, Family: "XC");
var results = (PumpHit[])await Call("pumps/search", duty);
var pump = results.First(item => item.Size == "50-200");
Check(pump.Series == "XC" && pump.Family == "CZ" && pump.Curve == "OH_750" &&
    pump.OriginalCurve == "CZ_50-200_B_CZ50-200/2", "Selected pump lost its series or curve numbers.");
var legacy = (PumpHit[])await Call("pumps/search", duty with { Family = "CZ" });
Check(legacy.SequenceEqual(results), "The source family no longer resolves existing selections.");
var allPumps = (PumpHit[])await Call("pumps/search", duty with { Family = null });
Check(allPumps.All(pump => !unavailable.ContainsKey(pump.Family)), "An unavailable series appeared in unfiltered selection.");
var water = (PumpHit[])await Call("pumps/search", duty with { ViscosityCp = 1 });
Check(water.SequenceEqual(results), "Water-viscosity selections regressed.");
foreach (var invalid in new[] { 0.0, -1, 3001 })
{
    try
    {
        await Call("pumps/search", duty with { ViscosityCp = invalid });
        throw new InvalidOperationException("Invalid viscosity was accepted.");
    }
    catch (ArgumentException) { }
}

// Metric benchmark from ANSI/HI 9.6.7-2021, Section 9.6.7.4.5.
var reference = new HydcalcCurve { Header = ["", "", "", "", "volute", "", ".2", ".16", "49.166666666666664"] };
reference.Metadata["TrimInfo"] = "BIT;0;0;0;0;0;0;0;0";
reference.Points.AddRange(new[]
{
    new HydcalcPoint { FlowM3h = 66, Head = 87.3, Efficiency = 60.2, Npshr = 2.55 },
    new HydcalcPoint { FlowM3h = 88, Head = 83, Efficiency = 66, Npshr = 3.1 },
    new HydcalcPoint { FlowM3h = 110, Head = 77, Efficiency = 68, Npshr = 4.15 },
    new HydcalcPoint { FlowM3h = 132, Head = 69.7, Efficiency = 66, Npshr = 6.25 }
});
Check(HydcalcSizing.TryEvaluate(reference, null, 2, 2950, 1, 103.2, new HydcalcSizingOptions(),
    out var referenceHead, out var referenceEfficiency, out var referenceNpsh, 120), "HI reference point failed.");
Check(Math.Abs(referenceHead / 2 - 72.2) < .15 && Math.Abs(referenceEfficiency - 50.2) < .15 &&
    referenceNpsh > 3.8, "HI reference head per stage, efficiency or NPSH differs.");

var viscousDuty = duty with { Density = 900, ViscosityCp = 36 };
var viscous = (PumpHit[])await Call("pumps/search", viscousDuty);
var corrected = viscous.Single(item => item.Curve == pump.Curve && item.Rpm == pump.Rpm);
var waterAtDensity = ((PumpHit[])await Call("pumps/search", duty with { Density = 900 }))
    .Single(item => item.Curve == pump.Curve && item.Rpm == pump.Rpm);
Check(corrected.Diameter > pump.Diameter && corrected.Efficiency < pump.Efficiency &&
    corrected.Power > waterAtDensity.Power && corrected.Npshr > pump.Npshr &&
    Math.Abs(corrected.Head - viscousDuty.Head) < 1e-5, "Viscosity did not change the actual CZ selection as expected.");
var restricted = (PumpHit[])await Call("pumps/search", viscousDuty with { Npsha = 1.15 * (pump.Npshr + corrected.Npshr) / 2 });
Check(!restricted.Any(item => item.Curve == corrected.Curve && item.Rpm == corrected.Rpm),
    "NPSHa filter did not use the corrected NPSHr.");
var viscousReport = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty = viscousDuty, index = corrected.Index }), json);
Check(viscousReport.GetProperty("report").GetProperty("viscosityCst").GetDouble() == 40 &&
    viscousReport.GetProperty("report").GetProperty("viscosityFactors")[2].GetDouble() < 1, "Missing viscosity report values.");
using (var curve = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty = viscousDuty, index = corrected.Index }, save: true))
    Check(IsPdf(curve), "Missing viscous curve PDF.");
foreach (var otherDuty in new[] { new PumpRequest(30, 50, Family: "X"), new PumpRequest(30, 50, Family: "XMB") })
{
    var baseline = (PumpHit[])await Call("pumps/search", otherDuty);
    var oil = (PumpHit[])await Call("pumps/search", otherDuty with { ViscosityCp = 25 });
    Check(oil.Any(result => baseline.Any(wet => wet.Curve == result.Curve && wet.Rpm == result.Rpm &&
        wet.Stages == result.Stages && wet.Efficiency > result.Efficiency && result.Power > wet.Power)),
        "No matched viscosity correction for " + otherDuty.Family);
}

var index = pump.Index;
var curves = JsonSerializer.SerializeToElement(await Call("pumps/curves", new { duty, index }), json);
Check(curves.GetProperty("plots").GetArrayLength() == 3, "Expected all three curves.");
var headingPlot = curves.GetProperty("printPlots")[0].GetString()!;
Check(headingPlot.Contains("XC 50-200") && headingPlot.Contains(pump.Curve) && !headingPlot.Contains("CZ50-200/2"),
    "The printable plot must only label the new curve number.");
using (var curve = (WorkbenchDownload)await Call("pumps/export-pdf", new { duty, index }, save: true))
    Check(IsPdf(curve) && curve.Name == "Curve_XC50-200_2950RPM_V0.pdf", "Missing named curve PDF.");
using (var package = (WorkbenchDownload)await Call("pumps/export-batch", new[] { new { duty, index }, new { duty, index } }, save: true))
using (var zip = new ZipArchive(package.Content, ZipArchiveMode.Read))
    Check(zip.Entries.Count == 2 && zip.Entries.All(entry => entry.FullName.StartsWith("Curve/")) &&
        zip.Entries.Select(entry => entry.FullName).Distinct().Count() == 2, "Missing batch curves.");

var models = (string[])await Submit("pump-models", new { duty, index });
const string model = "CZ50-200";
Check(models.Contains(model), "The selection didn't match a ZTools model.");
await Submit("new-from-pump", new { duty, index, model,
    details = new { tag = "OFFLINE-01", service = "Test service", material = "S-5" } });
var project = ((ProjectEntry[])await Submit("projects")).Single();
routes = new OfflineRoutes(directory);
var opened = (OfflineState)await Submit("open", new { id = project.Id });
Check(opened.Rows.Single().Model == model, "Device-local project could not be reopened.");
const string companyName = "\u5927\u8fde\u51ef\u4e50\u6cf5\u4e1a\u6709\u9650\u516c\u53f8";
await Change("project", ("I53", "HG20615"), ("K10", companyName));
var projectOptions = (ParameterForm)await Submit("parameters", new { group = "project", row = 0 });
var flangeField = projectOptions.Fields.Single(field => field.Address == "I53");
Check(flangeField.Value == "HG/T 20615" && flangeField.Choices.Contains("HG/T 20615"),
    "The legacy flange standard is not recognized in project settings.");
await Change("motor", ("S21", "YBX3-160M2-2"), ("U21", "160M"), ("V21", "380V"));
await Change("configuration", ("E21", "10\u7248"), ("AA21", "CZ\u8f7b\u8f74"), ("Y21", "11"), ("Z21", "\u65e0"));
var calculated = (OfflineState)await Submit("calculate");
Check(!calculated.NeedsCalculation, "Project calculation did not finish.");
var finished = (OfflineState)await Submit("generate", new { rows = new[] { 0 }, kinds = new[] { "GA", "SEC", "PID" } });
Check(finished.Files.Length == 3, "Expected GA, SEC and PID.");
foreach (var kind in new[] { "GA", "PID" })
{
    string scenePath = Directory.GetFiles(directory, kind + "__*.scene.json", SearchOption.AllDirectories).Single();
    var scene = JsonSerializer.Deserialize<PlotScene>(File.ReadAllText(scenePath), json)!;
    var text = scene.Items.Where(item => item.Type == "text").ToArray();
    Check(text.Count(item => item.Text == "HG/T 20615 Class150 RF") >= 2 &&
        text.All(item => !item.Text.Contains("HG20615 PN")), kind + " used legacy flange ratings.");
    if (kind != "PID") continue;
    var footer = text.Single(item => item.Text == companyName);
    var title = text.Single(item => item.Text == model);
    Check(footer.Align == "center" && Math.Abs(footer.Height - 4) < .01 &&
        Math.Abs(footer.Points[0][0] - 368.47) < .01 && Math.Abs(footer.Points[0][1] - 14.65) < .01,
        "PID company name is not centered in the GA/SEC footer position.");
    Check(title.Points[0][0] > 365 && Math.Abs(title.Points[0][1] - 36.27) < .01,
        "PID model is not above the drawing title.");
}
foreach (var output in finished.Files)
{
    using var pdf = (WorkbenchDownload)await Call($"submissions/file/pdf?id={output.Id}", save: true);
    using var dwg = (WorkbenchDownload)await Call($"submissions/file/dwg?id={output.Id}", save: true);
    Check(IsPdf(pdf) && dwg.Content.Length > 100, $"Missing {output.Kind} export.");
}
using (var files = (WorkbenchDownload)await Call("submissions/pdf-batch?multiple=true", save: true))
using (var zip = new ZipArchive(files.Content, ZipArchiveMode.Read))
    Check(zip.Entries.Select(entry => entry.FullName.Split('/')[0]).Order()
        .SequenceEqual(new[] { "GA", "PID", "SEC" }), "Missing drawing ZIP folders.");
using (var xlsx = (WorkbenchDownload)await Call("submissions/file/xlsx", save: true))
    Check(xlsx.Content.Length > 200, "Missing project workbook export.");

string fixture;
using (var workbook = new XSSFWorkbook())
using (var stream = new MemoryStream())
{
    var sheet = workbook.CreateSheet("Datasheet");
    void Set(int r, int c, object value)
    {
        var cell = (sheet.GetRow(r - 1) ?? sheet.CreateRow(r - 1)).CreateCell(c);
        if (value is double number) cell.SetCellValue(number);
        else cell.SetCellValue((string)value);
    }
    foreach (var (r, c, header) in new[] { (7, 1, "位号"), (7, 2, "泵型号"), (7, 6, "数量"),
        (8, 7, "流量 m3/h"), (8, 8, "扬程 m"), (8, 9, "温度 ℃"), (8, 11, "Viscosity mPas"),
        (8, 12, "density kg/m³"), (8, 13, "pressure MPa A"), (8, 15, "NPSHa/NPSHr"),
        (10, 9, "正常"), (10, 10, "最高"), (10, 13, "入口压力"), (10, 14, "出口压力"),
        (10, 15, "a"), (10, 16, "r"), (9, 18, "冲洗方案"), (7, 30, "备注") })
        Set(r, c, header);
    Set(4, 0, "用户consumer：Test client");
    for (int row = 11; row <= 12; row++)
    {
        Set(row, 1, "EXCEL-" + row); Set(row, 3, "Process liquid"); Set(row, 4, "Water");
        Set(row, 6, 2d); Set(row, 7, 35d); Set(row, 8, 55d); Set(row, 9, 50d); Set(row, 10, 60d);
        Set(row, 11, 1d); Set(row, 12, 1000d); Set(row, 13, .1d); Set(row, 15, 15d);
        Set(row, 16, 99d); Set(row, 18, "Plan 11"); Set(row, 21, "S-5"); Set(row, 29, "380V; IP55");
    }
    Set(11, 22, .75d);
    var percent = workbook.CreateCellStyle();
    percent.DataFormat = workbook.CreateDataFormat().GetFormat("0%");
    sheet.GetRow(10).GetCell(22).CellStyle = percent;
    Set(13, 0, 3d);
    sheet.GetRow(12).CreateCell(26).SetCellFormula("1/0");
    Set(14, 1, "NEEDS-HEAD"); Set(14, 7, 35d);
    Set(15, 0, "配置说明"); Set(16, 7, 999d);
    workbook.Write(stream, leaveOpen: true);
    fixture = Convert.ToBase64String(stream.ToArray());
    File.WriteAllBytes(Path.Combine(directory, "pump-datasheet-fixture.xlsx"), stream.ToArray());
}
var importedRows = (ImportedPumpRow[])await Call("pumps/import-datasheet", new { content = fixture });
Check(importedRows.Length == 3 && importedRows[0] is { SourceRow: 11, Duty: { Flow: 35, Head: 55, Density: 1000,
    ViscosityCp: 1, Npsha: 15, Temperature: 50 }, Details: { Tag: "EXCEL-11", Client: "Test client", Quantity: 2 } } &&
    importedRows[0].Issues.Length == 0 && importedRows[^1].Issues.Any(issue => issue.Contains("扬程")) &&
    importedRows[0].Details.ProposalNotes.Any(note => note.Contains("380V; IP55")) &&
    importedRows[0].Details.ProposalNotes.Contains("原表效率（%）：75"), "Fixed datasheet mapping failed.");
if (Environment.GetEnvironmentVariable("CFL_DATASHEET_TEST_INPUT") is { Length: > 0 } samplePath)
{
    var sampleRows = (ImportedPumpRow[])await Call("pumps/import-datasheet",
        new { content = Convert.ToBase64String(File.ReadAllBytes(samplePath)) });
    Check(sampleRows.Length == 4 && sampleRows.Select(row => row.Duty.Head).SequenceEqual(new double?[] { 21, 26, 28, 31 }) &&
        sampleRows.All(row => row is { Duty: { Flow: 2800, Density: 1150, Temperature: 50, ViscosityCp: 2, Npsha: 15 },
            Details.Quantity: 2, Issues.Length: 0 }) && sampleRows[2].Details.ProposalNotes.Any(note => note.Contains("6000V")),
        "The filled example did not preserve its hydraulic inputs and motor notes.");
    foreach (var row in sampleRows)
    {
        var candidates = (PumpHit[])await Call("pumps/search", row.Duty);
        Console.WriteLine($"Example Excel row {row.SourceRow}: {candidates.Length} hydraulic matches; " +
            $"{candidates.Count(hit => hit.Family is "Z" or "CZ")} in ZTools-supported families.");
    }
}
var batchRows = new List<object>();
foreach (var row in importedRows.Take(2))
{
    var hit = ((PumpHit[])await Call("pumps/search", row.Duty)).First(p => p.Family == "CZ" && p.Size == "50-200");
    batchRows.Add(new { row.Duty, hit.Index, model, row.Details });
}
var importedProject = (OfflineState)await Submit("new-from-batch", new { items = batchRows });
await Change("project", ("I53", "HG20615"));
Check(importedProject.Rows.Length == 2 && importedProject.Rows.All(row => row.ReviewPending),
    "Each imported equipment row must begin unconfirmed.");
var forms = JsonSerializer.SerializeToElement(await Submit("batch-parameters"), json);
Check(forms[0].GetProperty("proposalNotes").EnumerateArray().Any(note => note.GetString()!.Contains("Plan 11")),
    "Proposal references did not reach the ZTools per-item adjustment form.");
var updates = new List<ParameterChanges>();
for (int i = 0; i < 2; i++)
{
    var r = 21 + i;
    void Edit(string group, params (string Col, string Value)[] pairs) => updates.Add(new(group, i,
        pairs.Select(pair => new InputChange("input", pair.Col + r, pair.Value)).ToArray()));
    Edit("motor", ("S", "YBX3-160M2-2"), ("U", "160M"), ("V", "380V"));
    Edit("configuration", ("E", "10版"), ("AA", "CZ轻轴"), ("Y", "11"), ("Z", "无"));
}
updates.Add(new("review", 0, [new InputChange("CFL-App", "I21", "confirmed")]));
var edited = (OfflineState)await Submit("save-batch", updates);
Check(!edited.Rows[0].ReviewPending && edited.Rows[1].ReviewPending, "Review must apply to just one item.");
try
{
    await Submit("calculate");
    throw new InvalidOperationException("Calculation allowed an unconfirmed equipment row.");
}
catch (SubmissionInputException error)
{
    Check(error.Issues.Any(issue => issue.Row == 1 && issue.Address == "CFL-App!I22"),
        "ZTools did not identify the unconfirmed equipment row.");
}
await Submit("save-batch", new[] { new ParameterChanges("review", 1, [new InputChange("CFL-App", "I22", "confirmed")]) });
var readyBatch = (OfflineState)await Submit("calculate");
Check(!readyBatch.NeedsCalculation && readyBatch.Rows.All(row => !row.ReviewPending), "Confirmed batch did not calculate.");
var batchDrawings = (OfflineState)await Submit("generate", new { rows = new[] { 0, 1 }, kinds = new[] { "GA", "SEC", "PID" } });
Check(batchDrawings.Files.Length == 6 && batchDrawings.Files.GroupBy(f => f.Kind).All(kind => kind.Count() == 2),
    "Two confirmed imported rows did not generate GA, SEC and PID independently.");
var changed = (OfflineState)await Submit("save", new { group = "configuration", row = 1,
    changes = new[] { new InputChange("input", "Z22", "") } });
Check(!changed.Rows[0].ReviewPending && changed.Rows[1].ReviewPending,
    "An equipment edit must reopen confirmation for that row.");
Console.WriteLine("Offline pump search, plots, PDFs, project reopening, calculation and drawing exports passed.");

async Task Change(string group, params (string Address, string Value)[] fields) =>
    _ = await Submit("save", new { row = 0, group, changes = fields.Select(field => new InputChange("input", field.Address, field.Value)) });

static bool IsPdf(WorkbenchDownload file)
{
    Span<byte> prefix = stackalloc byte[5];
    return file.Content.Read(prefix) == prefix.Length && prefix.SequenceEqual("%PDF-"u8);
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
