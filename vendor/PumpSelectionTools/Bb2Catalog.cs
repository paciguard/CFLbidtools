using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PumpSelectionTools
{
    public sealed record Bb2Trace(string Quantity, string Unit, string Status, double[] DiametersMm, double[][][] Segments);

    public sealed record Bb2Source(string CurveNumber, string Family, string Size, int Rpm, int Stages,
        string SourceFile, int SourcePage, string SourceModel, string OriginalCurveNumber, string WorkbookLabel,
        string SourceSha256, string[] Notes, string[] SelectionBlockers, Bb2Trace[] Traces)
    {
        public bool AvailableForSelection => SelectionBlockers.Length == 0;
        public double[] DiametersMm => Traces.Where(trace => trace.Quantity == "head")
            .SelectMany(trace => trace.DiametersMm).Distinct().Order().ToArray();
    }

    public static class Bb2Catalog
    {
        private static readonly Lazy<Bb2Source[]> Rows = new(() =>
        {
            using var data = typeof(Bb2Catalog).Assembly.GetManifestResourceStream("PumpSelectionTools.Hydcalc.BB2Curves.json")
                ?? throw new FileNotFoundException("Missing bundled BB2 curve data.");
            return JsonSerializer.Deserialize<Bb2Source[]>(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Invalid bundled BB2 curve data.");
        });

        private sealed record SelectionSource(Bb2Source Row, HydcalcRecord Property, HydcalcCurve Curve,
            PdfTrimCurves Reference, double MinFlow, double MaxFlow, double MaxHead);

        private static readonly Lazy<SelectionSource[]> Selectable = new(() =>
            Records.Where(row => row.AvailableForSelection).Select(Build).ToArray());

        public static IReadOnlyList<Bb2Source> Records => Rows.Value;

        private static SelectionSource Build(Bb2Source row)
        {
            double[][] Points(string quantity, double diameter) =>
                row.Traces.SingleOrDefault(trace => trace.Quantity == quantity &&
                    trace.DiametersMm.SequenceEqual(new[] { diameter }))?.Segments.Single() ?? Array.Empty<double[]>();

            var impellers = row.DiametersMm.Select(diameter =>
                new PdfImpeller(diameter, Points("head", diameter), Points("shaft_power", diameter), Array.Empty<double[]>())
                { Efficiency = Points("efficiency", diameter) }).ToArray();
            var npsh = row.Traces.Single(trace => trace.Quantity == "npshr" && trace.DiametersMm.Length == 0)
                .Segments.Single();
            var reference = new PdfTrimCurves(row.SourceFile, row.SourcePage, row.CurveNumber, row.Rpm, impellers)
            {
                Stages = row.Stages, SharedNpshr = npsh, UsesPlottedEfficiency = true
            };
            var property = new HydcalcRecord { Id = row.CurveNumber, Fields = new[] { row.Family, row.Size } };
            var curve = new HydcalcCurve { Id = property.Id, Name = row.CurveNumber, Header = property.Fields,
                NumberAssignment = new(property.Id, row.OriginalCurveNumber, row.CurveNumber, row.Family) };
            var head = impellers.SelectMany(impeller => impeller.Head);
            return new(row, property, curve, reference, npsh[0][0], npsh[^1][0], head.Max(point => point[1]));
        }

        public static IEnumerable<HydcalcSelection> Search(HydcalcDuty duty, string family, string apiType)
        {
            if (duty.ViscosityCst > 1 || (!string.IsNullOrWhiteSpace(family) &&
                !family.Trim().Equals("HDD", StringComparison.OrdinalIgnoreCase) &&
                !family.Trim().Equals("KBB", StringComparison.OrdinalIgnoreCase)))
                yield break;
            foreach (var source in Selectable.Value)
            {
                var row = source.Row;
                if ((!string.IsNullOrWhiteSpace(family) && !row.Family.Equals(family.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                    !PumpFamilyPolicy.MatchesApiType(row.Family, apiType) || duty.Flow < source.MinFlow ||
                    duty.Flow > source.MaxFlow || duty.Head > source.MaxHead ||
                    (duty.FixedRpm is int rpm && rpm != row.Rpm) || !HydcalcSizing.MatchesPublishedSpeed(duty, row.Rpm))
                    continue;
                var match = HydcalcSizing.FromPdf(source.Property, source.Curve, source.Reference, duty,
                    new HydcalcSizingOptions(), row.Rpm);
                if (match != null) yield return match;
            }
        }
    }
}
