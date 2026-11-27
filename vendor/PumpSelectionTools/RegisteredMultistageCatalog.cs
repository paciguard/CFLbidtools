using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace PumpSelectionTools
{
    public sealed record RegisteredMultistageCurve(string CurveNumber, string RegisterCode, string ApiType, string[] PumpTypes,
        string Size, string SourceSize, int Rpm, int? ReferenceStages, int[] StageLimits, int[] SpecialStages,
        double? MinimumFlowM3h, string SourceFile, string SourceModel, string OriginalCurveNumber, string SourceSha256,
        string SourceKind, string[] SelectionBlockers, string[] Notes, Bb2Trace[] Traces)
    {
        public bool AvailableForSelection => SelectionBlockers.Length == 0;
        public IEnumerable<string> ApplicableSeries => ApiType == "BB4" &&
            RegisterCode.StartsWith("BB4_LO_", StringComparison.OrdinalIgnoreCase) ? PumpTypes.Append("XMR") : PumpTypes;
        public double[] DiametersMm => Traces.Where(trace => trace.Quantity == "head")
            .SelectMany(trace => trace.DiametersMm).Distinct().Order().ToArray();
        internal IEnumerable<int> AllowedStages => Enumerable.Range(StageLimits[0], StageLimits[1] - StageLimits[0] + 1)
            .Concat(SpecialStages).Distinct().Order();

        internal static string SourceFamily(string series) => series switch
        {
            "XMC" => "MC",
            "XMD" => "MD",
            "XMR" => "GSG",
            "XDA" => "SCSK",
            _ => throw new InvalidDataException("Unknown registered series: " + series)
        };
    }

    public static class RegisteredMultistageCatalog
    {
        private static readonly Lazy<RegisteredMultistageCurve[]> Rows = new(() =>
        {
            using var stream = typeof(RegisteredMultistageCatalog).Assembly
                .GetManifestResourceStream("PumpSelectionTools.Hydcalc.BB34Curves.json.gz")
                ?? throw new FileNotFoundException("Missing BB3/BB4 register curves.");
            using var uncompressed = new GZipStream(stream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<RegisteredMultistageCurve[]>(uncompressed,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Invalid bundled BB3/BB4 register curves.");
        });

        private sealed record SelectionSource(RegisteredMultistageCurve Row, string Series,
            HydcalcRecord Property, HydcalcCurve Curve, PdfTrimCurves Reference, double[][] MaximumHead);

        private static readonly Lazy<SelectionSource[]> Sources = new(() =>
            Records.Where(row => row.AvailableForSelection).SelectMany(Build).ToArray());

        public static IReadOnlyList<RegisteredMultistageCurve> Records => Rows.Value;

        private static IEnumerable<SelectionSource> Build(RegisteredMultistageCurve row)
        {
            var diameters = row.DiametersMm;
            double[][] Points(string quantity, double diameter) =>
                row.Traces.SingleOrDefault(trace => trace.Quantity == quantity &&
                    trace.DiametersMm.SequenceEqual(new[] { diameter }))?.Segments.Single() ?? Array.Empty<double[]>();
            var impellers = diameters.Select(diameter => new PdfImpeller(diameter, Points("head", diameter),
                Points("shaft_power", diameter), Points("npshr", diameter))
                { Efficiency = Points("efficiency", diameter) }).ToArray();
            var reference = new PdfTrimCurves(row.SourceFile, 0, row.CurveNumber, row.Rpm, impellers)
            {
                ReferenceStages = row.ReferenceStages, MaximumImpellerOnly = true,
                MinimumFlowM3h = row.MinimumFlowM3h, UsesPlottedEfficiency = impellers[^1].Efficiency.Length > 0
            };
            foreach (string series in row.ApplicableSeries)
            {
                var family = RegisteredMultistageCurve.SourceFamily(series);
                var property = new HydcalcRecord
                {
                    Id = row.CurveNumber + "-" + series,
                    Fields = new[] { family, row.Size, "", "", "", "", "", "", "", "horizontal",
                        row.StageLimits[1].ToString(CultureInfo.InvariantCulture), row.StageLimits[0].ToString(CultureInfo.InvariantCulture) }
                };
                var curve = new HydcalcCurve
                {
                    Id = property.Id, Name = row.SourceModel, Header = property.Fields.Take(2).ToArray(),
                    NumberAssignment = new(property.Id, row.OriginalCurveNumber, row.CurveNumber, series)
                };
                var assignedReference = series == "XMR" ? reference with { SharedHydraulicApiType = row.ApiType } : reference;
                yield return new(row, series, property, curve, assignedReference, impellers[^1].Head);
            }
        }

        public static IEnumerable<HydcalcSelection> Search(HydcalcDuty duty, string family, string apiType)
        {
            if (duty.Clearance != null || !double.IsFinite(duty.ViscosityCst) || duty.ViscosityCst is <= 0 or > 1 ||
                !double.IsFinite(duty.Flow) || duty.Flow <= 0 || !double.IsFinite(duty.Head) || duty.Head <= 0 ||
                !double.IsFinite(duty.Density) || duty.Density <= 0 || duty.Hz is not (50 or 60) ||
                (duty.Npsha is double available && (!double.IsFinite(available) || available < 0)))
                yield break;
            var filter = family?.Trim();
            var options = new HydcalcSizingOptions();
            foreach (var source in Sources.Value)
            {
                var row = source.Row;
                if ((!string.IsNullOrEmpty(filter) && !source.Series.Equals(filter, StringComparison.OrdinalIgnoreCase) &&
                        !source.Property.Field(0).Equals(filter, StringComparison.OrdinalIgnoreCase)) ||
                    !PumpFamilyPolicy.MatchesApiType(source.Property.Field(0), apiType) ||
                    (duty.FixedRpm is int rpm && rpm != row.Rpm) || !HydcalcSizing.MatchesPublishedSpeed(duty, row.Rpm) ||
                    row.MinimumFlowM3h is double minimum && duty.Flow < minimum ||
                    PdfTrimCurves.AtFlow(source.MaximumHead, duty.Flow) is not (> 0))
                    continue;

                // Only the maximum impeller has a complete measured set; never infer ancillary traces for smaller trims.
                foreach (var stages in row.AllowedStages)
                {
                    if (duty.Stages is int wanted && wanted != stages) continue;
                    var reference = source.Reference with { Stages = stages };
                    var match = HydcalcSizing.FromPdf(source.Property, source.Curve, reference, duty, options, row.Rpm);
                    if (match != null) yield return match;
                }
            }
        }
    }
}
