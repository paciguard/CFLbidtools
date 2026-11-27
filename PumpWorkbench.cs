using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using PumpSelectionTools;
using ZTools.Offline;

public sealed record PumpRequest(double Flow, double Head, double Density = 1000, int Hz = 50,
    int? Rpm = null, int? Poles = null, double? Npsha = null, double? Temperature = null,
    string? Family = null, string? ApiType = null, double? ViscosityCp = null, string? XSubtype = null,
    int? Stages = null, string? Clearance = null)
{
    internal HydcalcClearance? RequestedClearance()
    {
        if (Stages is <= 0) throw new ArgumentException("级数必须为正整数。");
        if (string.IsNullOrWhiteSpace(Clearance) || string.Equals(Clearance.Trim(), "All", StringComparison.OrdinalIgnoreCase))
            return null;
        var value = HydcalcClearances.Read(Clearance) ??
            throw new ArgumentException("间隙类型必须为 Standard、API 或 Hot。");
        if (value == HydcalcClearance.Hot && !PumpWorkbench.HasHotClearance)
            throw new ArgumentException("目录中没有 Hot（热态）间隙曲线，不能按热态条件选型。");
        return value;
    }

    internal double KinematicViscosity()
    {
        if (ViscosityCp is not double value) return 1;
        if (!double.IsFinite(Density) || Density <= 0)
            throw new ArgumentException("Density must be greater than zero to convert viscosity.");
        double cst = value * (1000 / Density);
        if (!double.IsFinite(cst) || cst <= 0)
            throw new ArgumentException("Viscosity must be a positive number in cP.");
        if (cst > 3000)
            throw new ArgumentException("Viscosity exceeds 3,000 cSt after conversion using density. Request manufacturer data.");
        return cst;
    }
}
public sealed record PumpRatingOverride(double? Efficiency = null, double? Npshr = null);
public sealed record CurveRequest(PumpRequest Duty, int Index, int Width = 960, double PreferredMin = .7, double PreferredMax = 1.2,
    PumpRatingOverride? RatingOverride = null);
public sealed record CurvePdfRequest(PumpRequest Duty, int Index, PumpProjectDetails? Details, PumpRatingOverride? RatingOverride = null);
public sealed record PumpTrimSample(double FlowM3h, double? HeadM, double? ShaftPowerKw, double? EfficiencyPercent,
    double? NpshrM, string? NpshrSource);
public sealed record PumpTrimPoint(double Diameter, double? Head, double? Efficiency, double? Npshr, string? NpshrSource,
    bool NpshrInterpolated, PumpTrimSample[] Samples);
public sealed record PumpTrimPreview(string Series, string Size, string Curve, int Rpm, int SourceRpm,
    string SourceFile, int SourcePage, double Flow, double RequestedHead, PumpTrimPoint[] Points, string[] Plots);
public sealed record PumpHit(int Index, string Family, string Size, string ApiType, string Suction,
    string FlowType, string Group, string Curve, int Stages, int Rpm, double MinDiameter, double Diameter,
    double MaxDiameter, double Head, double Efficiency, double Npshr, double Power, double? MotorKw,
    string Orientation, string OriginalCurve, string Series, string? XSubtype = null,
    int? SourceRpm = null, bool InterpolatedTrim = false, string? NpshrSource = null, int? SourcePage = null,
    StageRange? StageLimits = null, string? Clearance = null, PumpRatingOverride? RatingOverride = null,
    string? SharedHydraulicApiType = null)
{
    public string ModelSeries => XSubtype ?? Series;
}

internal sealed record CurveLibraryEntry(string Id, string Family, string Series, bool AvailableForSelection,
    string Size, string Name, string CurveNumber, string OriginalCurveNumber, double? Rpm,
    double? MinDiameterMm, double? MaxDiameterMm, int Points, IReadOnlyList<string> Issues,
    AhlstarSourceRecord? Source = null, string? SourceFile = null, double[]? TracedDiametersMm = null,
    string? Clearance = null, int? SourcePage = null);

public sealed class PumpWorkbench
{
    private static readonly string[] XSubtypes = ["XA", "XE", "XF", "XZX", "XGP"];
    internal static bool HasHotClearance => HydcalcCatalog.Bundled.Curves.Any(curve =>
        curve.AvailableForSelection && curve.Clearance == HydcalcClearance.Hot);

    private static readonly Dictionary<int, double[]> MotorRatings50Hz = new()
    {
        [3000] = [.75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315, 355, 375],
        [1500] = [.55, .75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315, 355, 375],
        [1000] = [.37, .55, .75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250, 280, 315],
        [750] = [.75, 1.1, 1.5, 2.2, 3, 4, 5.5, 7.5, 11, 15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200, 220, 250],
        [600] = [15, 18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185, 200],
        [500] = [18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160, 185],
        [429] = [18.5, 22, 30, 37, 45, 55, 75, 90, 110, 132, 160],
        [375] = [22, 30, 37, 45, 55, 75, 90, 110, 132]
    };

    public PumpWorkbench()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            PumpTableCache.Load();
            PumpCurveDataCache.Load();
            foreach (var size in PumpTableCache.Rows.Where(row => row.PumpType == "Z").Select(row => row.PumpSize).Distinct())
                HydcalcZSelection.MatchCurve(size);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    public object Options()
    {
        var catalog = HydcalcCatalog.Bundled;
        var sourceFamilies = catalog.Properties.Select(p => p.Field(0)).Append("Z").Distinct()
            .Where(family => SeriesEnabled(family, catalog.GetSeries(family))).ToArray();
        var verifiedPages = AhlstarCatalog.VerifiedTraces.Select(curve => curve.Page).ToHashSet();
        var ahlstarFamilies = AhlstarCatalog.Bundled.Records.Where(curve => verifiedPages.Contains(curve.Page)).Select(curve => curve.Family);
        var bb2 = Bb2Catalog.Records.Where(curve => curve.AvailableForSelection);
        var families = sourceFamilies.Select(family => PumpFamilyPolicy.DisplaySeries(family, catalog.GetSeries(family)))
            .Concat(ahlstarFamilies).Concat(bb2.Select(curve => curve.Series)).Append(SmCatalog.Series)
            .Concat(RegisteredMultistageCatalog.Records.Where(curve => curve.AvailableForSelection).SelectMany(curve => curve.ApplicableSeries))
            .Distinct().Order().ToArray();
        var apiTypes = sourceFamilies.Concat(bb2.Select(curve => curve.Family).Distinct()).Append(SmCatalog.Family)
            .Select(PumpFamilyPolicy.GetApiTypeLabel)
            .SelectMany(type => type.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(type => type.Length > 0).Distinct().Order().ToList();
        apiTypes.RemoveAll(type => type is "ISO" or "ANSY" or "\u6d41\u7a0b\u6cf5");
        if (sourceFamilies.Contains("CZ")) apiTypes.Add(PumpFamilyPolicy.GetApiTypeLabel("CZ"));
        return new { families, apiTypes, xSubtypes = XSubtypes, hotClearanceAvailable = HasHotClearance };
    }

    public object Catalog(string? family, string? query, int page)
    {
        bool Contains(string value) => string.IsNullOrWhiteSpace(query) ||
            value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
        var ahlstar = AhlstarCatalog.Bundled;
        var verified = AhlstarCatalog.VerifiedTraces.ToDictionary(curve => curve.Page);
        var aReference = PdfTrimCurves.For("K50551");
        var aSource = ahlstar.Records.Single(curve => curve.Page == aReference.Page);
        CurveLibraryEntry Imported(AhlstarSourceRecord source)
        {
            verified.TryGetValue(source.Page, out var data);
            var diameters = source.TrimDiametersMm.Length != 0 ? source.TrimDiametersMm : source.PlottedDiametersMm ?? [];
            return new("AHLSTAR-" + source.Page, source.Family, source.Family, data != null, source.Size,
                source.CurveNumber, source.CurveNumber, source.CurveNumber, source.Rpm,
                diameters.Length == 0 ? null : diameters.Min(), diameters.Length == 0 ? null : diameters.Max(),
                data?.Impellers.Sum(impeller => impeller.Head.Length) ?? 0, [],
                source, ahlstar.SourceFile, data?.Impellers.Select(i => i.DiameterMm).ToArray() ?? source.PlottedDiametersMm);
        }
        var all = HydcalcCatalog.Bundled.Curves.Select(curve =>
            new CurveLibraryEntry(curve.Id, curve.Family,
                PumpFamilyPolicy.DisplaySeries(curve.Family, curve.AvailableForSelection ? curve.Series : curve.Family, curve),
                curve.AvailableForSelection, curve.Size, curve.Name, curve.CurveNumber, curve.OriginalCurveNumber,
                curve.Rpm, curve.Name == "K50551" ? aReference.MinDiameter : curve.MinDiameterMm,
                curve.MaxDiameterMm, curve.Points.Count, curve.Issues,
                curve.Name == "K50551" ? aSource : null, curve.Name == "K50551" ? ahlstar.SourceFile : null,
                curve.Name == "K50551" ? aReference.Impellers.Select(i => i.DiameterMm).ToArray() : null,
                HydcalcClearances.Label(curve.Clearance)))
            .Concat(ahlstar.Records.Where(source => source.Page != aSource.Page).Select(Imported))
            .Concat(Bb2Catalog.Records.Select(source =>
            {
                var diameters = source.DiametersMm;
                return new CurveLibraryEntry(source.PreviousCurveNumber, source.Family, source.Series, source.AvailableForSelection,
                    source.Size, source.WorkbookLabel, source.CurveNumber, source.OriginalCurveNumber,
                    source.Rpm, diameters.Min(), diameters.Max(),
                    source.Traces.Sum(trace => trace.Segments.Sum(segment => segment.Length)),
                    source.SelectionBlockers.Concat(source.Notes).ToArray(),
                    SourceFile: source.SourceFile, TracedDiametersMm: diameters, SourcePage: source.SourcePage);
            }))
            .Concat(SmCatalog.Bundled.Records.Select(source =>
            {
                var diameters = source.Impellers.Select(impeller => impeller.DiameterMm).ToArray();
                return new CurveLibraryEntry(source.CurveNumber, SmCatalog.Family, SmCatalog.Series,
                    source.AvailableForSelection, source.Size, SmCatalog.Family + source.Size, source.CurveNumber,
                    source.OriginalCurveNumber, source.Rpm, diameters.Length == 0 ? null : diameters[0],
                    diameters.Length == 0 ? null : diameters[^1], source.Impellers.Sum(impeller => impeller.Head.Length),
                    source.AvailableForSelection ? ["原型 SM · 扫描图估算"] : ["原型 SM · 仅登记，性能曲线待核验"],
                    SourceFile: SmCatalog.Bundled.SourceFile, TracedDiametersMm: diameters, SourcePage: source.SourcePage);
            }))
            .Concat(RegisteredMultistageCatalog.Records.Select(source =>
            {
                var diameters = source.DiametersMm;
                var series = string.Join(" / ", source.ApplicableSeries);
                var notes = source.SelectionBlockers.Concat(source.Notes);
                if (source.ApplicableSeries.Contains("XMR"))
                    notes = notes.Append("XMR (BB5): shared BB4 hydraulics for preliminary selection.");
                return new CurveLibraryEntry(source.CurveNumber, source.ApiType == "BB3" ? "SCSK" : series, series,
                    source.AvailableForSelection, source.Size, source.SourceModel, source.CurveNumber, source.OriginalCurveNumber,
                    source.Rpm, diameters.Length == 0 ? null : diameters[0], diameters.Length == 0 ? null : diameters[^1],
                    source.Traces.Sum(trace => trace.Segments.Sum(segment => segment.Length)),
                    notes.ToArray(), SourceFile: source.SourceFile, TracedDiametersMm: diameters);
            })).ToArray();
        var curves = all.Where(curve =>
            (curve.Series.Split(" / ", StringSplitOptions.None).Any(series => MatchesSeries(family, curve.Family, series)) ||
                (!curve.AvailableForSelection && string.Equals(family?.Trim(), HydcalcCatalog.UnavailableSeries, StringComparison.OrdinalIgnoreCase))) &&
            (Contains(curve.CurveNumber) || Contains(curve.OriginalCurveNumber) || Contains(curve.Name) || Contains(curve.Size) ||
                (curve.SourceFile is string filename && Contains(filename)) ||
                (curve.Source?.NominalSize is string nominal && Contains(nominal)))).ToArray();
        return new
        {
            total = curves.Length, page, pageSize = 30,
            families = all.SelectMany(curve => curve.Series.Split(" / ", StringSplitOptions.None)).Distinct().Order(),
            records = curves.Skip((page - 1) * 30).Take(30)
        };
    }

    public PumpHit[] Search(PumpRequest duty)
    {
        var matches = Find(duty, out var subtype);
        return matches.Select((item, index) => Summarize(item, duty.Density, duty.Hz, index, subtype)).ToArray();
    }

    public PumpTrimPreview[] TrimPreview(PumpRequest duty) => BuildTrimPreviews(duty, includePlots: true);

    private static PumpTrimPreview[] BuildTrimPreviews(PumpRequest duty, bool includePlots)
    {
        var clearance = duty.RequestedClearance();
        if (string.IsNullOrWhiteSpace(duty.Family) || !double.IsFinite(duty.Flow) || duty.Flow <= 0 ||
            !double.IsFinite(duty.Head) || duty.Head <= 0 || duty.KinematicViscosity() > 1)
            return [];

        var previews = new List<PumpTrimPreview>();
        foreach (var curve in HydcalcCatalog.Bundled.Curves)
        {
            if (!curve.AvailableForSelection || !MatchesSeries(duty.Family, curve.Family, curve.Series) ||
                !PumpFamilyPolicy.MatchesApiType(curve.Family, duty.ApiType) ||
                !curve.Metadata.TryGetValue("ReferenceDiametersMm", out var diameters) ||
                curve.MaxDiameterMm is not > 0 || curve.Rpm is not > 0 || curve.Issues.Count != 0)
                continue;

            var reference = PdfTrimCurves.For(curve.CurveNumber);
            if (reference == null || (duty.Stages is int stages && stages != reference.Stages) ||
                (clearance != null && curve.Clearance != clearance)) continue;
            var rpm = duty.Rpm ?? (int)Math.Round(curve.Rpm.Value);
            if (rpm != reference.Rpm && Math.Abs(rpm - curve.Rpm.Value) > 1e-6) continue;
            if (!HydcalcSizing.Speeds.Keys.Any(motor =>
            {
                double synchronous = 120.0 * duty.Hz / motor.Item2;
                return motor.Item1 == duty.Hz && (duty.Poles == null || duty.Poles == motor.Item2) &&
                    rpm >= .90 * synchronous && rpm <= 1.02 * synchronous;
            })) continue;

            var listed = ParseDiameters(diameters);
            var points = reference.Impellers.Where(impeller => listed.Contains(impeller.DiameterMm))
                .Select(impeller => ReadTrimPoint(curve, reference, impeller, rpm, duty.Flow)).ToArray();
            if (points.Length != 0)
                previews.Add(new(curve.Series, curve.Size, curve.CurveNumber, rpm, reference.Rpm, reference.SourceFile,
                    reference.Page, duty.Flow, duty.Head, points, includePlots ? TrimPlots(points, duty.Flow) : []));
        }
        return previews.ToArray();
    }

    private static double[] ParseDiameters(string? value) => (value ?? "").Split(';')
        .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var diameter) ? diameter : 0)
        .Where(diameter => double.IsFinite(diameter) && diameter > 0).Distinct().ToArray();

    private static PumpTrimPoint ReadTrimPoint(HydcalcCurve original, PdfTrimCurves reference,
        PdfImpeller impeller, int rpm, double ratedFlow)
    {
        var samples = reference.Samples(original, rpm, impeller.DiameterMm, ratedFlow)
            .Select(p => new PumpTrimSample(p.Flow, p.Head, p.WaterPower, p.Efficiency, p.Npshr, p.NpshrSource)).ToArray();
        var duty = samples.First(point => point.FlowM3h == ratedFlow);
        return new(impeller.DiameterMm, duty.HeadM, duty.EfficiencyPercent, duty.NpshrM, duty.NpshrSource,
            impeller.Npshr.Length == 0, samples);
    }

    private static string[] TrimPlots(PumpTrimPoint[] points, double dutyFlow)
    {
        var plotSpecs = new (string Title, string Axis, Func<PumpTrimSample, double?> Value)[]
        {
            ("Head by impeller diameter", "Head (m)", row => row.HeadM),
            ("Efficiency (derived from chart H and power)", "Efficiency (%)", row => row.EfficiencyPercent),
            ("NPSHr (dashed = diameter interpolation)", "NPSHr (m)", row => row.NpshrM)
        };
        OxyColor[] colors = [OxyColors.Black, OxyColor.FromRgb(0, 102, 173), OxyColor.FromRgb(186, 59, 29),
            OxyColor.FromRgb(0, 123, 98), OxyColor.FromRgb(136, 96, 0)];
        double maxFlow = points.Max(point => point.Samples.Max(sample => sample.FlowM3h));
        double flowLimit = Math.Ceiling(maxFlow / 10) * 10;
        var rendered = new string[plotSpecs.Length];
        for (int index = 0; index < plotSpecs.Length; index++)
        {
            var spec = plotSpecs[index];
            var model = new PlotModel { Title = spec.Title, TitleFontSize = 14, PlotMargins = new OxyThickness(69, double.NaN, 19, 36) };
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Flow (m3/h)", Minimum = 0, Maximum = flowLimit,
                MajorGridlineStyle = LineStyle.Dot });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = spec.Axis, Minimum = 0,
                AxisTitleDistance = 12, MaximumPadding = .06, MajorGridlineStyle = LineStyle.Dot });
            if (index == 0)
                model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopCenter, LegendPlacement = LegendPlacement.Outside,
                    LegendOrientation = LegendOrientation.Horizontal, LegendFontSize = 11, LegendItemSpacing = 8 });
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                var line = new LineSeries { Title = "D" + point.Diameter.ToString("0.##", CultureInfo.InvariantCulture),
                    Color = colors[i % colors.Length], LineStyle = index == 2 && point.NpshrInterpolated ? LineStyle.Dash : LineStyle.Solid,
                    StrokeThickness = 1.8 };
                line.Points.AddRange(point.Samples.Select(sample => spec.Value(sample) is double value
                    ? new DataPoint(sample.FlowM3h, value) : DataPoint.Undefined));
                model.Series.Add(line);
            }
            PlotCurveFit.Apply(model, index == 2 ? dutyFlow : null);
            rendered[index] = ExportPlot(model, 620, index == 0 ? 275 : 205);
        }
        return rendered;
    }

    internal static WorkbenchDownload ExportTrimData(PumpRequest duty)
    {
        var previews = BuildTrimPreviews(duty, includePlots: false);
        if (previews.Length == 0) throw new ArgumentException("当前工况没有可导出的叶轮估算数据。");
        var data = new
        {
            purpose = "Preliminary estimates only. Do not use for final selection or NPSH verification.",
            model = "Head and shaft power digitized from the PDF. Water efficiency from hydraulic power / shaft power. " +
                "NPSHr for intermediate diameters interpolated at equal source flow. Earlier supplied D330 data fills gaps in its PDF trace. " +
                "Speed correction uses Q times n/n0, head and NPSHr times (n/n0)^2, and power times (n/n0)^3.",
            curves = previews.Select(curve => new
            {
                curve.Series, curve.Size, curve.Curve, curve.Rpm, curve.SourceRpm, curve.SourceFile, curve.SourcePage,
                impellers = curve.Points.Select(point => new { diameterMm = point.Diameter, point.NpshrInterpolated, point.Samples })
            })
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return new("Impeller_Trim_Curves.json", "application/json", new MemoryStream(bytes, writable: false));
    }

    public PumpHit GetSelection(PumpRequest duty, int index, PumpRatingOverride? ratingOverride = null)
    {
        var matches = Find(duty, out var subtype);
        if (index < 0 || index >= matches.Count) throw new ArgumentException("Select a valid pump result.");
        var selection = matches[index];
        return ApplyRating(Summarize(selection, duty.Density, duty.Hz, index, subtype), duty, selection, ratingOverride);
    }

    public object Curves(CurveRequest request)
    {
        var (plots, summary, report) = BuildReport(request);
        var width = Math.Clamp(request.Width, 360, 1200);
        return new
        {
            pump = summary,
            report,
            plots = plots.Select((plot, index) => ExportPlot(plot, width, index == 0 ? 380 : 190)).ToArray(),
            printPlots = plots.Select(ExportPrintPlot).ToArray()
        };
    }

    internal static WorkbenchDownload ExportPdfBatch(CurvePdfRequest[] requests, CancellationToken cancellation = default)
    {
        if (requests is not { Length: >= 1 and <= 30 } || requests.Any(request => request?.Duty == null))
            throw new ArgumentException("批量导出须包含 1 至 30 项有效选型。");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                cancellation.ThrowIfCancellationRequested();
                using var document = CreateCurvePdf(request);
                string stem = "Curve/" + Path.GetFileNameWithoutExtension(document.Name);
                string path = stem + ".pdf";
                for (int suffix = 2; !used.Add(path); suffix++) path = stem + $" ({suffix}).pdf";
                using var entry = zip.CreateEntry(path).Open();
                document.Content.CopyTo(entry);
            }
        }
        return new("Curves_V0.zip", "application/zip", new MemoryStream(output.ToArray(), writable: false));
    }

    internal static WorkbenchDownload CreateCurvePdf(CurvePdfRequest request)
    {
        var (plots, pump, report) = BuildReport(new(request.Duty, request.Index, RatingOverride: request.RatingOverride));
        string name = ReportFileName.Curve(request.Details?.Tag, request.Details?.Service, pump.ModelSeries + pump.Size, pump.Rpm);
        return new(name + ".pdf", "application/pdf",
            new MemoryStream(PumpCurvePdf.Create(plots, pump, request.Duty, report, request.Details), writable: false));
    }

    internal static (PlotModel[] Plots, PumpHit Pump, PumpReportMetrics Report) BuildReport(CurveRequest request)
    {
        var results = Find(request.Duty, out var subtype);
        if (request.Index < 0 || request.Index >= results.Count) throw new ArgumentException("Select a valid pump result.");
        if (!double.IsFinite(request.PreferredMin) || !double.IsFinite(request.PreferredMax) ||
            request.PreferredMin < 0 || request.PreferredMax <= request.PreferredMin)
            throw new ArgumentException("Preferred flow limits are invalid.");
        var pump = results[request.Index];
        var density = request.Duty.Density;
        var baseline = Summarize(pump, density, request.Duty.Hz, request.Index, subtype);
        var summary = ApplyRating(baseline, request.Duty, pump, request.RatingOverride);
        var plots = HydcalcSelectionPlotter.Create(pump, density, Efficiency(pump),
            preferredMin: request.PreferredMin, preferredMax: request.PreferredMax, series: summary.ModelSeries);
        if (summary.RatingOverride != null) AdjustRatingPlots(plots, request.Duty, baseline, summary);
        var flowPlots = pump.ViscosityCst > 1 ? plots : new[] { plots[0] };
        var maximum = flowPlots.SelectMany(plot => plot.Series.OfType<LineSeries>()).SelectMany(series => series.Points)
            .Where(point => double.IsFinite(point.X)).Max(point => point.X);
        var stepEstimate = maximum / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(stepEstimate)));
        var step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.First(value => value * magnitude >= stepEstimate) * magnitude;
        var limit = Math.Ceiling(maximum / step) * step;
        foreach (var plot in plots)
        {
            var flow = plot.Axes.Single(axis => axis.Position == AxisPosition.Bottom);
            flow.Minimum = 0;
            flow.Maximum = limit;
            flow.MajorStep = step;
            flow.MinorStep = step / 5;
            plot.PlotMargins = new OxyThickness(58, double.NaN, 65, 45);
            plot.Background = OxyColors.Transparent;
        }

        var report = PumpReportMetrics.Read(plots, summary, request.Duty, pump, MotorSizingMargin(summary.Power));
        for (int index = 0; index < plots.Length; index++)
            PlotCurveFit.Apply(plots[index], index == 1 && pump.PdfReference != null ? pump.Flow : null);
        return (plots, summary, report);
    }

    private static double ShaftPower(PumpRequest duty, double flow, double head, double efficiency) =>
        duty.Density * 9.81 * flow / 3600 * head / (efficiency / 100) / 1000;

    private static PumpHit ApplyRating(PumpHit baseline, PumpRequest duty, HydcalcSelection selection, PumpRatingOverride? rating)
    {
        if (rating is null or { Efficiency: null, Npshr: null }) return baseline;
        if (rating.Efficiency is double eta && (!double.IsFinite(eta) || eta <= 0 || eta > 100))
            throw new ArgumentException("效率须大于 0 且不超过 100%。");
        if (rating.Npshr is double npshr)
        {
            if (!double.IsFinite(npshr) || npshr <= 0 || baseline.Npshr <= 0 || !double.IsFinite(npshr / baseline.Npshr))
                throw new ArgumentException("NPSHr 须为有效的正数（m）。");
            if (duty.Npsha is double available && npshr * selection.Options.NpshMargin > available + 1e-9)
                throw new ArgumentException("修改后的 NPSHr 不满足当前工况的 NPSH 余量要求。");
        }
        double efficiency = rating.Efficiency ?? baseline.Efficiency;
        double power = rating.Efficiency.HasValue ? ShaftPower(duty, duty.Flow, baseline.Head, efficiency) : baseline.Power;
        if (!double.IsFinite(power) || !double.IsFinite(power * 1.5))
            throw new ArgumentException("效率过低，无法计算有效的轴功率。");
        return baseline with
        {
            Efficiency = efficiency, Npshr = rating.Npshr ?? baseline.Npshr, Power = power,
            MotorKw = rating.Efficiency.HasValue ? MotorRating(power, baseline.Rpm, duty.Hz) : baseline.MotorKw,
            RatingOverride = rating
        };
    }

    private static void AdjustRatingPlots(PlotModel[] plots, PumpRequest duty, PumpHit baseline, PumpHit adjusted)
    {
        LineSeries Line(int panel, string title) => plots[panel].Series.OfType<LineSeries>().Single(line => line.Title == title);
        void AdjustLine(LineSeries line, double rated, Func<DataPoint, double> adjust)
        {
            for (int i = 0; i < line.Points.Count; i++)
            {
                var point = line.Points[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) continue;
                double value = Math.Abs(point.X - duty.Flow) < 1e-9 ? rated : adjust(point);
                if (!double.IsFinite(value) || !double.IsFinite(value * 1.5))
                    throw new ArgumentException("手动输入超出可绘制的曲线范围。");
                line.Points[i] = new DataPoint(point.X, value);
            }
        }
        void UpdateDutyMarkerAndAxes(int panel, LineSeries line, double value, double conversion)
        {
            var plot = plots[panel];
            foreach (var marker in plot.Annotations.Where(annotation => Equals(annotation.Tag, "RatedPointTriangle")).ToArray())
                plot.Annotations.Remove(marker);
            HydcalcSelectionPlotter.AddDutyMarker(plot, duty.Flow, value);
            double maximum = line.Points.Where(point => double.IsFinite(point.Y))
                .Select(point => point.Y).DefaultIfEmpty(value).Max();
            double scale = Math.Max(maximum, value) * 1.1;
            if (!double.IsFinite(scale * conversion)) throw new ArgumentException("手动输入超出可绘制的曲线范围。");
            plot.Axes.Single(axis => axis.Position == AxisPosition.Left).Maximum = scale;
            plot.Axes.Single(axis => axis.Position == AxisPosition.Right).Maximum = scale * conversion;
        }

        if (adjusted.RatingOverride!.Efficiency.HasValue)
        {
            double factor = adjusted.Efficiency / baseline.Efficiency;
            var efficiency = Line(0, "Efficiency (%)");
            var head = plots[0].Series.OfType<LineSeries>().Single(line => line.Title.StartsWith("Rated D=", StringComparison.Ordinal));
            var power = Line(2, "Shaft power");
            AdjustLine(efficiency, adjusted.Efficiency, point => Math.Min(100, point.Y * factor));
            efficiency.Title = "Efficiency (%) (manual)";
            // Recalculate each flowing point; retain the shutoff power shape where hydraulic power is zero.
            AdjustLine(power, adjusted.Power, point =>
                point.X > 0 && PumpReportMetrics.Interpolate(efficiency.Points, point.X) is > 0 and var eta &&
                PumpReportMetrics.Interpolate(head.Points, point.X) is > 0 and var h
                    ? ShaftPower(duty, point.X, h, eta)
                    : point.Y / factor);
            UpdateDutyMarkerAndAxes(2, power, adjusted.Power, 1.34102209);
        }
        if (adjusted.RatingOverride.Npshr.HasValue)
        {
            var npsh = Line(1, "NPSHr");
            double factor = adjusted.Npshr / baseline.Npshr;
            AdjustLine(npsh, adjusted.Npshr, point => point.Y * factor);
            plots[1].Subtitle = "Manually adjusted NPSHr; verify with manufacturer";
            UpdateDutyMarkerAndAxes(1, npsh, adjusted.Npshr, 3.280839895);
        }
    }

    private static string ExportPlot(PlotModel plot, int width, int height) =>
        new SvgExporter { Width = width, Height = height, IsDocument = false }.ExportToString(plot);

    private static string ExportPrintPlot(PlotModel plot, int index)
    {
        if (index != 0)
        {
            foreach (var axis in plot.Axes.Where(axis => axis.Position is AxisPosition.Left or AxisPosition.Right))
            {
                axis.FontSize = 10;
                axis.TitleFontSize = 10;
                axis.AxisTitleDistance += 6;
            }
        }
        return ExportPlot(plot, 735, index == 0 ? 365 : 130);
    }

    private static double Efficiency(HydcalcSelection item) =>
        item.Property.Field(0) == "Z" ? HydcalcZSelection.RatedEfficiency(item) : item.Efficiency;

    private static string SelectionSeries(HydcalcSelection item)
    {
        var family = item.Property.Field(0);
        var series = item.PdfReference != null ? item.Curve.Series : HydcalcCatalog.Bundled.GetSeries(family);
        return PumpFamilyPolicy.DisplaySeries(family, series, item.SuctionCurve ?? item.Curve);
    }

    private static PumpHit Summarize(HydcalcSelection item, double density, int hz, int index, string? subtype)
    {
        var family = item.Property.Field(0);
        var series = SelectionSeries(item);
        var efficiency = Efficiency(item);
        var power = item.PdfReference?.UsesPlottedEfficiency == true ? item.PowerKw :
            density * 9.81 * item.Flow / 3600 * item.Head / (efficiency / 100) / 1000;
        var motor = MotorRating(power, item.Rpm, hz);
        var minimum = item.PdfReference?.MinDiameter ?? item.Curve.MinDiameterMm!.Value;
        var maximum = item.PdfReference?.MaxDiameter ?? item.Curve.MaxDiameterMm!.Value;
        if (item.SuctionCurve != null)
            minimum = Math.Max(minimum, item.SuctionCurve.MinDiameterMm!.Value / item.SuctionCurve.MaxDiameterMm!.Value * maximum);
        return new PumpHit(index, family, item.Property.Field(1), PumpFamilyPolicy.GetApiTypeLabel(family),
            series == "MSD2" ? "Double" : PumpFamilyPolicy.GetSuctionLabel(family), item.FlowType, item.HydraulicGroup,
            item.HydraulicLabel, item.Stages, item.Rpm, minimum, item.DiameterRatio * maximum,
            maximum, item.Head, efficiency, item.Npshr, power, motor,
            item.Property.Field(9), item.OriginalHydraulicLabel, series, subtype,
            item.PdfReference?.Rpm, item.PdfReference?.IsPublishedDiameter(item.DiameterRatio * maximum) == false,
            item.NpshrSource, item.PdfReference is { Page: > 0 } reference ? reference.Page : null, item.StageLimits,
            HydcalcClearances.Label(item.Curve.Clearance), SharedHydraulicApiType: item.PdfReference?.SharedHydraulicApiType);
    }

    private static double? MotorRating(double power, int rpm, int hz)
    {
        if (hz != 50) return null;
        var synchronous = MotorRatings50Hz.Keys.MinBy(speed => Math.Abs((double)rpm / speed - 1));
        if (rpm < synchronous * .90 || rpm > synchronous * 1.02) return null;
        var required = power * (1 + MotorSizingMargin(power) / 100);
        return MotorRatings50Hz[synchronous].Where(rating => rating >= required).Select(rating => (double?)rating).FirstOrDefault();
    }

    private static double MotorSizingMargin(double power) => power <= 22 ? 25 : power >= 55 ? 10 : 15;

    private static bool SeriesEnabled(string family, string series) => PumpFamilyPolicy.IsEnabled(family) &&
        !string.IsNullOrWhiteSpace(series) && !string.Equals(series, HydcalcCatalog.UnavailableSeries, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSeries(string? filter, string family, string series)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var key = filter.Trim().ToUpperInvariant();
        if (series is "MSD" or "MSD2")
            return series == (key is "MSDD" or "MSD-D" or "MSD-DD" ? "MSD2" : key);
        return string.Equals(series, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(family, key, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveXSubtype(PumpRequest duty)
    {
        if (string.IsNullOrWhiteSpace(duty.XSubtype)) return null;
        if (string.IsNullOrWhiteSpace(duty.Family) || !MatchesSeries(duty.Family, "Z", "X"))
            throw new ArgumentException("X 子型号仅适用于 X 系列。");
        return XSubtypes.FirstOrDefault(type => string.Equals(type, duty.XSubtype.Trim(), StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("X 子型号必须为 XA、XE、XF、XZX 或 XGP。");
    }

    private static List<HydcalcSelection> Find(PumpRequest duty, out string? subtype)
    {
        subtype = ResolveXSubtype(duty);
        if (duty.Poles is int poles && !HydcalcSizing.Speeds.ContainsKey(Tuple.Create(duty.Hz, poles)))
            throw new ArgumentException("Choose a supported pole count.");
        double viscosity = duty.KinematicViscosity();
        var clearance = duty.RequestedClearance();
        var input = new HydcalcDuty
        {
            Flow = duty.Flow, Head = duty.Head, Density = duty.Density, Hz = duty.Hz, FixedRpm = duty.Rpm,
            Poles = duty.Poles, Npsha = duty.Npsha, TemperatureC = duty.Temperature, ViscosityCst = viscosity,
            Stages = duty.Stages, Clearance = clearance
        };
        var catalog = HydcalcCatalog.Bundled;
        var imported = HydcalcSizing.Search(catalog, input);
        var zPumps = HydcalcZSelection.Search(duty.Flow, duty.Head, duty.Npsha, duty.Hz, duty.Poles, duty.Rpm,
            viscosity, duty.Stages, clearance);
        return zPumps.Concat(imported).Concat(AhlstarCatalog.Bundled.Search(input, duty.Family, duty.ApiType))
            .Concat(Bb2Catalog.Search(input, duty.Family, duty.ApiType))
            .Concat(SmCatalog.Search(input, duty.Family, duty.ApiType))
            .Concat(RegisteredMultistageCatalog.Search(input, duty.Family, duty.ApiType)).Where(item =>
        {
            var family = item.Property.Field(0);
            var series = SelectionSeries(item);
            return SeriesEnabled(family, series) && item.Curve.AvailableForSelection &&
                item.SuctionCurve?.AvailableForSelection != false && PumpFamilyPolicy.MatchesApiType(family, duty.ApiType) &&
                MatchesSeries(duty.Family, family, series);
        }).OrderByDescending(Efficiency).ThenBy(item => item.Property.Field(0)).ThenBy(item => item.Property.Field(1)).ToList();
    }
}
