using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PumpSelectionTools
{
    public sealed record SmSource(string CurveNumber, string Size, int Rpm, string OriginalCurveNumber,
        int SourcePage, PdfImpeller[] Impellers)
    {
        public bool AvailableForSelection => Impellers.Length > 0;
    }

    public sealed record SmCatalog(string SourceFile, string SourceSha256, SmSource[] Records)
    {
        public const string Family = "SM";
        public const string Series = "XDC";

        private static readonly Lazy<SmCatalog> Data = new(() =>
        {
            using var stream = typeof(SmCatalog).Assembly.GetManifestResourceStream("PumpSelectionTools.Hydcalc.SMCurves.json")
                ?? throw new FileNotFoundException("Missing bundled SM curve data.");
            return JsonSerializer.Deserialize<SmCatalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Invalid bundled SM curve data.");
        });

        private static readonly Lazy<SelectionSource[]> Sources = new(() =>
            Bundled.Records.Where(row => row.AvailableForSelection).Select(row =>
            {
                var reference = new PdfTrimCurves(Bundled.SourceFile, row.SourcePage, row.CurveNumber, row.Rpm, row.Impellers);
                var property = new HydcalcRecord { Id = row.CurveNumber, Fields = new[]
                    { Family, row.Size, "", "", "", "", "", "", "", "horizontal" } };
                var curve = new HydcalcCurve
                {
                    Id = row.CurveNumber, Name = row.CurveNumber, Header = property.Fields.Take(2).ToArray(),
                    NumberAssignment = new(row.CurveNumber, row.OriginalCurveNumber, row.CurveNumber, Series)
                };
                var head = row.Impellers.SelectMany(impeller => impeller.Head);
                return new SelectionSource(reference, property, curve, head.Max(point => point[0]), head.Max(point => point[1]));
            }).ToArray());

        private sealed record SelectionSource(PdfTrimCurves Reference, HydcalcRecord Property, HydcalcCurve Curve,
            double MaxFlow, double MaxHead);

        public static SmCatalog Bundled => Data.Value;

        public static IEnumerable<HydcalcSelection> Search(HydcalcDuty duty, string family, string apiType)
        {
            var filter = family?.Trim();
            if (!PumpFamilyPolicy.MatchesApiType(Family, apiType) ||
                (!string.IsNullOrEmpty(filter) && !filter.Equals(Family, StringComparison.OrdinalIgnoreCase) &&
                    !filter.Equals(Series, StringComparison.OrdinalIgnoreCase)) ||
                duty.Stages is not (null or 1) || duty.Clearance != null || duty.ViscosityCst > 1 ||
                !double.IsFinite(duty.Flow) || duty.Flow <= 0 || !double.IsFinite(duty.Head) || duty.Head <= 0 ||
                !double.IsFinite(duty.Density) || duty.Density <= 0 || (duty.Hz != 50 && duty.Hz != 60) ||
                (duty.Npsha is double available && (!double.IsFinite(available) || available < 0)))
                yield break;

            foreach (var source in Sources.Value)
            {
                var rpm = source.Reference.Rpm;
                if (duty.Flow > source.MaxFlow || duty.Head > source.MaxHead ||
                    (duty.FixedRpm is int fixedRpm && fixedRpm != rpm) || !HydcalcSizing.MatchesPublishedSpeed(duty, rpm))
                    continue;
                var match = HydcalcSizing.FromPdf(source.Property, source.Curve, source.Reference, duty, new HydcalcSizingOptions(), rpm);
                if (match != null) yield return match;
            }
        }
    }
}
