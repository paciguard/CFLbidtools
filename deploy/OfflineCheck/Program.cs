using System.IO.Compression;
using System.Text.Json;
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
Check(catalog.GetProperty("total").GetInt32() == 801, "The renumbered catalog is incomplete.");
var catalogCurves = HydcalcCatalog.Bundled.Curves;
Check(catalogCurves.All(curve => (curve.CurveNumber != curve.Name || curve.Name == "K50551") &&
        curve.OriginalCurveNumber != curve.Name) &&
    catalogCurves.Select(curve => curve.CurveNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 801,
    "A curve is missing a unique new number or its original number.");
var options = JsonSerializer.SerializeToElement(await Call("pumps/options"), json);
Check(options.GetProperty("families").EnumerateArray().Select(value => value.GetString()).SequenceEqual(
    new[] { "A", "OHH", "X", "XC", "XDA", "XDB", "XDC", "XMB", "XMC", "XMD", "XMR", "XTT" }),
    "Selection filters are not using the current pump series.");
var ahlstarCurve = catalogCurves.Single(curve => curve.Name == "K50551");
Check(ahlstarCurve.Family == "A" && ahlstarCurve.Size == "23-40" && ahlstarCurve.Points.Count == 40 &&
    ahlstarCurve.Issues.Count == 0 && HydcalcCatalog.Bundled.LinkedProperties(ahlstarCurve).Single().Field(0) == "A",
    "The A23-40 curve or hydraulic link is incomplete.");
var aDuty = new PumpRequest(30, 150, Family: "A");
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
Check(grouped.GetProperty("total").GetInt32() == 153, "The XDB series must group all four source families.");
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
