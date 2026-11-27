using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace PumpSelectionTools
{
    public sealed record AhlstarSourceRecord(int Page, string Family, string Size, string CurveNumber, string Kind,
        int? Rpm, int? MinRpm, int? MaxRpm, string NominalSize, string Impeller,
        double[] TrimDiametersMm, double[] PlottedDiametersMm);

    public sealed record AhlstarCatalog(string SourceFile, int PageCount, AhlstarSourceRecord[] Records)
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly Lazy<AhlstarCatalog> Index = new(() => Load<AhlstarCatalog>("AhlstarIndex.json"));
        private static readonly Lazy<PdfTrimCurves[]> Traces = new(() => Load<PdfTrimCurves[]>("AhlstarTraces.json")
            .Concat(Load<PdfTrimCurves[]>("Ahlstar33-100.json"))
            .Concat(Load<PdfTrimCurves[]>("AhlstarVector.json.gz"))
            .Concat(Load<PdfTrimCurves[]>("AhlstarRaster.json.gz")).ToArray());
        private static readonly Lazy<SelectionSource[]> Sources = new(BuildSources);
        public static AhlstarCatalog Bundled => Index.Value;
        public static IReadOnlyList<PdfTrimCurves> VerifiedTraces => Traces.Value;

        private static T Load<T>(string name)
        {
            using var stream = typeof(AhlstarCatalog).Assembly.GetManifestResourceStream("PumpSelectionTools.Hydcalc." + name)
                ?? throw new FileNotFoundException("Missing AHLSTAR data.", name);
            if (name.EndsWith(".gz", StringComparison.Ordinal))
            {
                using var expanded = new GZipStream(stream, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<T>(expanded, JsonOptions);
            }
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }

        private sealed record SelectionSource(AhlstarSourceRecord Row, PdfTrimCurves Reference,
            HydcalcRecord Property, HydcalcCurve Curve, double MaxFlow, double MaxHead);

        private static SelectionSource[] BuildSources()
        {
            var rows = Bundled.Records.ToDictionary(row => row.Page);
            var sources = new List<SelectionSource>();
            foreach (var reference in VerifiedTraces)
            {
                var row = rows[reference.Page];
                if (row.CurveNumber != reference.CurveNumber || row.Rpm != reference.Rpm || row.Kind != "fixed")
                    throw new InvalidDataException("Curve data do not match AHLSTAR source page " + row.Page);
                var property = new HydcalcRecord { Id = "AHLSTAR-" + row.Page, Fields = new[] { row.Family, row.Size } };
                var curve = new HydcalcCurve { Id = property.Id, Name = row.CurveNumber, Header = property.Fields };
                var head = reference.Impellers.SelectMany(impeller => impeller.Head).ToArray();
                sources.Add(new(row, reference, property, curve, head.Max(point => point[0]), head.Max(point => point[1])));
            }
            return sources.ToArray();
        }

        public IEnumerable<HydcalcSelection> Search(HydcalcDuty duty, string family, string apiType)
        {
            var seen = new HashSet<(string Family, string Size, string Curve, int Rpm)>();
            foreach (var source in Sources.Value)
            {
                var row = source.Row;
                var reference = source.Reference;
                if ((!string.IsNullOrWhiteSpace(family) && !row.Family.Equals(family.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    !PumpFamilyPolicy.MatchesApiType(row.Family, apiType) ||
                    duty.Flow > source.MaxFlow || duty.Head > source.MaxHead ||
                    (duty.FixedRpm.HasValue && duty.FixedRpm.Value != reference.Rpm) ||
                    !HydcalcSizing.MatchesPublishedSpeed(duty, reference.Rpm))
                    continue;

                var key = (row.Family, row.Size, row.CurveNumber, reference.Rpm);
                if (seen.Contains(key)) continue;
                var match = HydcalcSizing.FromPdf(source.Property, source.Curve, reference, duty, new HydcalcSizingOptions(), reference.Rpm);
                if (match != null && seen.Add(key)) yield return match;
            }
        }
    }
}
