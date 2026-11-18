using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PumpSelectionTools
{
    public static class PumpFamilyPolicy
    {
        private static readonly HashSet<string> ExcludedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MBci", "MD_sf", "SMH", "ZA", "ZE", "ZF",
            "BKC", "CAP8", "HZZ", "MSE"
        };

        private static readonly Dictionary<string, int> SuctionEyes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "BBT", 1 }, { "BBTD", 2 },
            { "CD", 2 }, { "CD8", 2 }, { "CZ", 1 }, { "GSG", 1 },
            { "MB", 1 }, { "MC", 1 }, { "MD", 1 }, { "MSD", 1 },
            { "MSD-D", 2 }, { "MSD-DD", 2 }, { "OHH", 1 },
            { "SMN", 2 }, { "TTMC", 1 }, { "VCR8", 1 }, { "Z", 1 }
        };

        private static readonly Dictionary<string, string[]> ApiTypes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "BBT", new[] { "BB2" } }, { "BBTD", new[] { "BB2" } },
            { "CD", new[] { "BB2" } }, { "CD8", new[] { "BB2" } },
            { "CZ", new[] { "ISO/ANSY/流程泵" } },
            { "GSG", new[] { "BB5" } },
            { "MB", new[] { "BB4" } }, { "MC", new[] { "BB4" } }, { "MD", new[] { "BB4" } },
            { "MSD", new[] { "BB3" } }, { "MSD-D", new[] { "BB3" } }, { "MSD-DD", new[] { "BB3" } },
            { "OHH", new[] { "OH2" } }, { "SMN", new[] { "BB1" } },
            { "TTMC", new[] { "VS6" } }, { "VCR8", new[] { "VS6", "VS1" } },
            { "Z", new[] { "OH1", "OH2" } }
        };

        public static bool IsEnabled(string family)
        {
            return !string.IsNullOrWhiteSpace(family) && !ExcludedFamilies.Contains(family.Trim());
        }

        public static int? GetSuctionEyes(string family)
        {
            int eyes;
            return !string.IsNullOrWhiteSpace(family) && SuctionEyes.TryGetValue(family.Trim(), out eyes)
                ? (int?)eyes : null;
        }

        public static string GetSuctionLabel(string family)
        {
            int? eyes = GetSuctionEyes(family);
            return eyes == 1 ? "Single" : eyes == 2 ? "Double" : "-";
        }

        public static string GetApiTypeLabel(string family)
        {
            string[] types;
            return !string.IsNullOrWhiteSpace(family) && ApiTypes.TryGetValue(family.Trim(), out types)
                ? string.Join(" / ", types) : "-";
        }

        public static bool MatchesApiType(string family, string requestedType)
        {
            if (!IsEnabled(family)) return false;
            if (string.IsNullOrWhiteSpace(requestedType) ||
                string.Equals(requestedType.Trim(), "All / 全部", StringComparison.OrdinalIgnoreCase))
                return true;
            string[] types;
            return ApiTypes.TryGetValue(family.Trim(), out types) &&
                types.Any(type => string.Equals(type, requestedType.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    // Source fields are retained verbatim: undocumented fields are not guessed.
    public sealed class HydcalcRecord
    {
        public string Id { get; set; }
        public string[] Fields { get; set; }
        public string Raw { get { return string.Join(";", Fields); } }
        public string Field(int index) { return index < Fields.Length ? Fields[index] : ""; }
    }

    public sealed class HydcalcPoint
    {
        public int Index { get; set; }
        public double FlowM3h { get; set; }
        public double Head { get; set; }
        public double Efficiency { get; set; }
        public double Auxiliary { get; set; }
        public double Npshr { get; set; }
        public string Raw { get; set; }
    }

    public sealed class HydcalcCurve
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string[] Header { get; set; }
        public string Family { get { return Header.Length > 0 ? Header[0] : ""; } }
        public string Size { get { return Header.Length > 1 ? Header[1] : ""; } }
        public string Variant { get { return Header.Length > 2 ? Header[2] : ""; } }
        public double? Rpm { get { return HeaderNumber(8, 60); } }
        public double? MaxDiameterMm { get { return HeaderNumber(6, 1000); } }
        public double? MinDiameterMm { get { return HeaderNumber(7, 1000); } }
        public List<HydcalcPoint> Points { get; private set; } = new List<HydcalcPoint>();
        public Dictionary<string, string> Metadata { get; private set; } = new Dictionary<string, string>();
        public List<string> Issues { get; private set; } = new List<string>();
        private double? HeaderNumber(int index, double factor)
        {
            double value;
            return index < Header.Length && HydcalcCatalog.TryNumber(Header[index], out value)
                ? (double?)(value * factor) : null;
        }
    }

    public sealed class HydcalcCatalogGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Families { get; private set; } = new List<string>();
        public int DeclaredCount { get; set; }
    }

    public sealed class HydcalcCatalog
    {
        public List<HydcalcCurve> Curves { get; private set; } = new List<HydcalcCurve>();
        public List<HydcalcRecord> Properties { get; private set; } = new List<HydcalcRecord>();
        public List<HydcalcRecord> Hydraulics { get; private set; } = new List<HydcalcRecord>();
        public List<HydcalcRecord> Liquids { get; private set; } = new List<HydcalcRecord>();
        public List<HydcalcCatalogGroup> Groups { get; private set; } = new List<HydcalcCatalogGroup>();
        public List<string> Issues { get; private set; } = new List<string>();

        private static readonly Lazy<HydcalcCatalog> bundled = new Lazy<HydcalcCatalog>(() => Load(name =>
        {
            var stream = typeof(HydcalcCatalog).Assembly.GetManifestResourceStream("PumpSelectionTools.Hydcalc." + name);
            if (stream == null) throw new FileNotFoundException("Missing bundled catalog: " + name);
            return new StreamReader(stream);
        }));
        public static HydcalcCatalog Bundled { get { return bundled.Value; } }

        public static HydcalcCatalog Load(Func<string, TextReader> open)
        {
            var result = new HydcalcCatalog();
            result.ReadRecords(open, "PumpProperties.txt", result.Properties);
            result.ReadRecords(open, "PumpHydraulics.txt", result.Hydraulics);
            result.ReadRecords(open, "liquids.txt", result.Liquids);
            result.ReadCurves(open);
            result.ReadGroups(open);
            result.ValidateLinks();
            return result;
        }

        internal static bool TryNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private void ReadRecords(Func<string, TextReader> open, string file, List<HydcalcRecord> target)
        {
            using (var reader = open(file))
            {
                string line;
                var ids = new HashSet<string>();
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("[") || line.StartsWith(";")) continue;
                    int equals = line.IndexOf('=');
                    if (equals < 1) { Issues.Add(file + ": malformed record: " + line); continue; }
                    string id = line.Substring(0, equals).Trim();
                    if (!ids.Add(id)) Issues.Add(file + ": duplicate ID " + id);
                    target.Add(new HydcalcRecord { Id = id, Fields = line.Substring(equals + 1).Split(';') });
                }
            }
        }

        private void ReadCurves(Func<string, TextReader> open)
        {
            using (var reader = open("Curves.txt"))
            {
                string line;
                HydcalcCurve curve = null;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        curve = new HydcalcCurve { Id = (Curves.Count + 1).ToString(CultureInfo.InvariantCulture),
                            Name = line.Substring(1, line.Length - 2), Header = new string[0] };
                        Curves.Add(curve);
                        continue;
                    }
                    int equals = line.IndexOf('=');
                    if (curve == null || equals < 1) { Issues.Add("Curves.txt: malformed line: " + line); continue; }
                    string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1);
                    int index;
                    if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    {
                        var fields = value.Split(';');
                        var numbers = new double[5];
                        bool valid = fields.Length >= 5;
                        for (int i = 0; i < 5 && valid; i++) valid = TryNumber(fields[i], out numbers[i]);
                        if (!valid) { curve.Issues.Add("Invalid point " + key + ": " + value); continue; }
                        curve.Points.Add(new HydcalcPoint { Index = index, FlowM3h = numbers[0] * 3600,
                            Head = numbers[1], Efficiency = numbers[2], Auxiliary = numbers[3], Npshr = numbers[4], Raw = value });
                    }
                    else
                    {
                        if (curve.Metadata.ContainsKey(key)) curve.Issues.Add("Repeated metadata key: " + key);
                        curve.Metadata[key] = value;
                        if (key == "HydrInfo") curve.Header = value.Split(';');
                    }
                }
            }
            foreach (var curve in Curves)
            {
                if (curve.Header.Length < 9 || !curve.Rpm.HasValue || curve.Rpm <= 0)
                    curve.Issues.Add("Missing or invalid hydraulic header.");
                if (curve.Points.Count < 3) curve.Issues.Add("Fewer than three valid points.");
                if (curve.Points.GroupBy(p => p.Index).Any(g => g.Count() > 1)) curve.Issues.Add("Duplicate point indices.");
                if (curve.Points.Any(p => p.FlowM3h < 0 || p.Efficiency < 0 || p.Efficiency > 100 || p.Npshr < 0))
                    curve.Issues.Add("Out-of-range flow, efficiency or NPSHr.");
                for (int i = 1; i < curve.Points.Count; i++)
                    if (curve.Points[i].FlowM3h < curve.Points[i - 1].FlowM3h)
                    { curve.Issues.Add("Flow is not monotonic in source order."); break; }
            }
            foreach (var group in Curves.GroupBy(c => c.Name).Where(g => g.Count() > 1))
            {
                string issue = "Repeated hydraulic name '" + group.Key + "': " + group.Count() + " sections retained separately; links are ambiguous.";
                Issues.Add(issue);
                foreach (var curve in group) curve.Issues.Add(issue);
            }
        }

        private void ReadGroups(Func<string, TextReader> open)
        {
            int declared = 0;
            using (var reader = open("Catalogs.txt"))
            {
                string line;
                HydcalcCatalogGroup group = null;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("[Catalog ") && line.EndsWith("]"))
                    {
                        group = new HydcalcCatalogGroup { Id = line.Substring(9, line.Length - 10) };
                        Groups.Add(group); continue;
                    }
                    int equals = line.IndexOf('=');
                    if (equals < 1) continue;
                    string key = line.Substring(0, equals), value = line.Substring(equals + 1);
                    if (key == "CatalogCount") int.TryParse(value, out declared);
                    if (group == null) continue;
                    if (key == "CatalogName") group.Name = value;
                    else if (key == "PumpCount") { int count; int.TryParse(value, out count); group.DeclaredCount = count; }
                    else if (key.StartsWith("Pump")) group.Families.Add(value);
                }
            }
            if (declared != Groups.Count) Issues.Add("Catalogs.txt declares " + declared + " catalogs but contains " + Groups.Count + ".");
            foreach (var group in Groups.Where(g => g.DeclaredCount != g.Families.Count)) Issues.Add("Catalog count mismatch: " + group.Name);
        }

        private void ValidateLinks()
        {
            var ids = new HashSet<string>(Properties.Select(p => p.Id));
            var names = new HashSet<string>(Curves.Select(c => c.Name));
            foreach (var link in Hydraulics)
            {
                if (!ids.Contains(link.Field(0))) Issues.Add("Hydraulic link " + link.Id + ": missing property " + link.Field(0));
                if (!names.Contains(link.Field(1))) Issues.Add("Hydraulic link " + link.Id + ": missing curve " + link.Field(1));
            }
        }

        public IEnumerable<HydcalcRecord> LinkedProperties(HydcalcCurve curve)
        {
            var ids = new HashSet<string>(Hydraulics.Where(h => h.Field(1) == curve.Name).Select(h => h.Field(0)));
            return Properties.Where(p => ids.Contains(p.Id));
        }
    }
}
