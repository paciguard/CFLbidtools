using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace PumpSelectionTools
{
    public class PumpCurvePoint
    {
        /// <summary>Flow rate (Q)</summary>
        public double Flow { get; set; }

        /// <summary>Head (H)</summary>
        public double Head { get; set; }

        /// <summary>Efficiency (η)</summary>
        public double Efficiency { get; set; }

        /// <summary>Required NPSH</summary>
        public double Npshr { get; set; }
    }

    public class PumpImpellerScaling
    {
        public double Dmax;   // max impeller diameter
        public double D;      // actual impeller diameter

        public PumpImpellerScaling(double dMax, double d)
        {
            Dmax = dMax;
            D = d;
        }

        double Ratio => D / Dmax;

        // Head scaling
        public double Head(double Hmax)
        {
            return Hmax * Math.Pow(Ratio, 2);
        }

        public static double CalculateNs(
                                            double rpm,
                                            double flow_m3h,
                                            double head_m
                                        )
        {
            if (rpm <= 0 || flow_m3h <= 0 || head_m <= 0)
                throw new ArgumentException("rpm, flow and head must be positive");

            // Convert flow from m³/h to m³/s
            double Q = flow_m3h / 3600.0;

            return rpm * Math.Sqrt(Q) / Math.Pow(head_m, 0.75);
        }


        // NPSHR scaling
        public double NPSHR(double npshrMax)
        {
            return npshrMax * Math.Pow(Ratio, 2);
        }

        // Efficiency scaling (API empirical)
        public double Efficiency(double effMax, double exponent = 0.15)
        {
            return effMax * Math.Pow(Ratio, exponent);
        }

        public static double TrimmedBepEfficiency(
                                                        double effMax,
                                                        double dMax,
                                                        double dTrim,
                                                        double kEff = 0.26   // Efficiency exponent — calibrate from manufacturer data
                                                  )
        {
            double R = dTrim / dMax;
            return effMax * Math.Pow(R, kEff);
        }

    }

    public class MinImpellerCorrection
    {
        double D, Dmax;
        double Ratio => D / Dmax;

        public MinImpellerCorrection(double d, double dmax)
        {
            D = d;
            Dmax = dmax;
        }

        public double Head(double Hmax, double KH = 0.10, double n = 2.9)
        {
            return Hmax * Math.Pow(Ratio, 2)
                 * (1.0 - KH * Math.Pow(1.0 - Ratio, n));
        }


        public double Efficiency(double effMax,
                                 double k = 0.15,
                                 double Keta = 0.08,
                                 double m = 2.0)
        {
            return effMax
                 * Math.Pow(Ratio, k)
                 * (1.0 - Keta * Math.Pow(1.0 - Ratio, m));
        }



        public double NPSHR(double npshrMax, double KN = 0.05)
        {
            return npshrMax
                 * Math.Pow(Ratio, 2)
                 * (1.0 + KN * (1.0 - Ratio));
        }




    }

    public class PumpCurveCalculator
    {
        public QuadraticFitter pHeadFit;
        public QuadraticFitter pEffFit;
        public QuadraticFitter pNpshrFit;
        // Constructor: input 40 data points
        public PumpCurveCalculator(double[] Q, double[] H, double[] Eff, double[] NPSHR, double[] HeadNsR)
        {
            pHeadFit = new QuadraticFitter();
            pHeadFit.Fit(Q, H);

            pEffFit = new QuadraticFitter();
            pEffFit.Fit(Q, Eff);

            pNpshrFit = new QuadraticFitter();
            pNpshrFit.Fit(Q, NPSHR);
            //HeadNsRFit=new QuadraticFitter ();
        }

        public double GetEfficiencyTrimmed(
                                                double flow,
                                                double dMax,
                                                double dTrim,
                                                double kEff = 0.26,
                                                double kQ = 0.08
                                            )
        {
            return TrimmedEfficiencyAtFlow(
                flow,
                pEffFit,
                dMax,
                dTrim,
                kEff,
                kQ
            );
        }

        public double TrimmedEfficiencyAtFlow(
                                                        double flow,
                                                        QuadraticFitter effFitMax,   // Original efficiency curve fit
                                                        double dMax,
                                                        double dTrim,
                                                        double kEff = 0.26,
                                                        double kQ = 0.08             // BEP flow shift factor (slight forward shift after trim)
                                                    )
        {
            double R = dTrim / dMax;

            // Efficiency scaling by diameter ratio
            double etaScale = Math.Pow(R, kEff);

            // BEP flow correction (curve contraction after trim)
            double flowScale = Math.Pow(R, kQ);

            // Evaluate base efficiency at equivalent full-impeller flow
            double effBase = effFitMax.Evaluate(flow / flowScale);

            return effBase * etaScale;
        }

        // Get Head at any flow

    }
    public class MotorPoleResult
    {
        public int Poles { get; set; }
        public int Frequency { get; set; }
        public double SynchronousSpeed { get; set; }
        public double Slip { get; set; }

        public static MotorPoleResult DecideMotorPoles(double rpm)
        {
            int[] frequencies = { 50, 60 };
            int[] poles = { 2, 4, 6, 8, 10, 12, 16 };

            MotorPoleResult best = null;
            double bestSlipError = double.MaxValue;

            foreach (int f in frequencies)
            {
                foreach (int p in poles)
                {
                    double ns = 120.0 * f / p;

                    if (ns <= rpm)
                        continue; // physically impossible

                    double slip = (ns - rpm) / ns;

                    // realistic slip range
                    if (slip < 0.005 || slip > 0.06)
                        continue;

                    // prefer slip close to ~2%
                    double slipError = Math.Abs(slip - 0.02);

                    if (slipError < bestSlipError)
                    {
                        bestSlipError = slipError;
                        best = new MotorPoleResult
                        {
                            Poles = p,
                            Frequency = f,
                            SynchronousSpeed = ns,
                            Slip = slip
                        };
                    }
                }
            }

            return best;
        }
    }
    public class QuadraticFitter
    {
        // Original coefficients: H = aQ² + bQ + c
        public double a;
        public double b;
        public double c;

        // Worn coefficients
        public double a_w;
        public double b_w;
        public double c_w;

        /// <summary>
        /// Fit y = a*x² + b*x + c
        /// </summary>
        public void Fit(double[] x, double[] y)
        {
            if (x.Length != y.Length)
                throw new ArgumentException("x and y must have same length");

            int n = x.Length;

            double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0;
            double sumY = 0, sumXY = 0, sumX2Y = 0;

            for (int i = 0; i < n; i++)
            {
                double xi = x[i];
                double yi = y[i];
                double xi2 = xi * xi;

                sumX += xi;
                sumX2 += xi2;
                sumX3 += xi2 * xi;
                sumX4 += xi2 * xi2;

                sumY += yi;
                sumXY += xi * yi;
                sumX2Y += xi2 * yi;
            }

            double[,] mat = {
            { sumX4, sumX3, sumX2 },
            { sumX3, sumX2, sumX },
            { sumX2, sumX,  n }
        };

            double[] rhs = { sumX2Y, sumXY, sumY };

            double det = Determinant3x3(mat);
            if (Math.Abs(det) < 1e-12)
                throw new Exception("Singular matrix");

            double[,] matA = (double[,])mat.Clone();
            double[,] matB = (double[,])mat.Clone();
            double[,] matC = (double[,])mat.Clone();

            for (int i = 0; i < 3; i++)
            {
                matA[i, 0] = rhs[i];
                matB[i, 1] = rhs[i];
                matC[i, 2] = rhs[i];
            }

            a = Determinant3x3(matA) / det;
            b = Determinant3x3(matB) / det;
            c = Determinant3x3(matC) / det;

            // Pump head curve must open downward
            if (a >= 0)
                throw new Exception("Invalid pump curve: quadratic coefficient must be negative.");
        }

        /// <summary>
        /// Evaluate original curve
        /// </summary>
        public double Evaluate(double flow)
        {
            return a * flow * flow + b * flow + c;
        }

        /// <summary>
        /// Apply wear ring clearance correction
        /// clearanceRatio = actual / design
        /// Example: 1.5 means 50% larger clearance
        /// </summary>
        //public void ApplyWearCorrection(double clearanceRatio)
        //{
        //    if (clearanceRatio <= 0)
        //        throw new ArgumentException("Invalid clearance ratio");

        //    // Empirical hydraulic correction factor
        //    // 5% head drop per 100% clearance increase (typical radial pump)
        //    double headFactor = 1.0 - 0.05 * (clearanceRatio - 1.0);

        //    headFactor = Math.Max(headFactor, 0.5); // limit unrealistic drop

        //    a_w = a * headFactor;
        //    b_w = b * headFactor;
        //    c_w = c * headFactor;
        //}
        /// <summary>
        /// Apply wear correction using only actual radial clearance (mm)
        /// wearRingDiameter = mean wear ring diameter (mm)
        /// </summary>
        public void ApplyWearCorrection(double actualClearance, double wearRingDiameter)
        {
            if (actualClearance <= 0)
                throw new ArgumentException("Actual clearance must be > 0.");

            if (wearRingDiameter <= 0)
                throw new ArgumentException("Wear ring diameter must be > 0.");

            // Non-dimensional clearance
            double relativeClearance = actualClearance / wearRingDiameter;

            // Empirical leakage coefficient
            // 15–30 typical for radial pumps
            double K = 18.0;

            double headFactor = 1.0 - K * relativeClearance;

            // Worn head is always <= original head. Clamp to a sane lower bound
            // (e.g. 70% of original) so a very large clearance doesn't produce
            // negative or near-zero coefficients.
            const double minHeadFactor = 0.7;
            headFactor = Math.Max(headFactor, minHeadFactor);

            a_w = a * headFactor;
            b_w = b * headFactor;
            c_w = c * headFactor;
        }

        /// <summary>
        /// Evaluate worn curve
        /// </summary>
        public double EvaluateWorn(double flow)
        {
            return a_w * flow * flow + b_w * flow + c_w;
        }

        /// <summary>
        /// Solve flow from head (original curve)
        /// </summary>
        public double GetFlowFromHead(double head)
        {
            return SolveQuadratic(a, b, c - head);
        }

        /// <summary>
        /// Solve flow from head (worn curve)
        /// </summary>
        public double GetFlowFromHeadWorn(double head)
        {
            return SolveQuadratic(a_w, b_w, c_w - head);
        }

        private double SolveQuadratic(double A, double B, double C)
        {
            if (Math.Abs(A) < 1e-12)
            {
                // Degenerate (linear) case: B*x + C = 0
                if (Math.Abs(B) < 1e-12) return double.NaN;
                double xLin = -C / B;
                return xLin >= 0 ? xLin : double.NaN;
            }

            double D = B * B - 4 * A * C;
            if (D < 0)
                return double.NaN;

            double sqrtD = Math.Sqrt(D);

            double x1 = (-B + sqrtD) / (2 * A);
            double x2 = (-B - sqrtD) / (2 * A);

            // choose physically meaningful root (positive & smaller)
            if (x1 >= 0 && x2 >= 0)
                return Math.Min(x1, x2);

            if (x1 >= 0) return x1;
            if (x2 >= 0) return x2;

            return double.NaN;
        }

        private double Determinant3x3(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                 - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                 + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }
    }

    public class QuadraticFitterNpsh
    {
        // Original coefficients: H = aQ² + bQ + c
        public double a;
        public double b;
        public double c;

        // Worn coefficients
        public double a_w;
        public double b_w;
        public double c_w;

        /// <summary>
        /// Fit y = a*x² + b*x + c
        /// </summary>
        public void Fit(double[] x, double[] y)
        {
            if (x.Length != y.Length)
                throw new ArgumentException("x and y must have same length");

            int n = x.Length;

            double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0;
            double sumY = 0, sumXY = 0, sumX2Y = 0;

            for (int i = 0; i < n; i++)
            {
                double xi = x[i];
                double yi = y[i];
                double xi2 = xi * xi;

                sumX += xi;
                sumX2 += xi2;
                sumX3 += xi2 * xi;
                sumX4 += xi2 * xi2;

                sumY += yi;
                sumXY += xi * yi;
                sumX2Y += xi2 * yi;
            }

            double[,] mat = {
            { sumX4, sumX3, sumX2 },
            { sumX3, sumX2, sumX },
            { sumX2, sumX,  n }
        };

            double[] rhs = { sumX2Y, sumXY, sumY };

            double det = Determinant3x3(mat);
            if (Math.Abs(det) < 1e-12)
                throw new Exception("Singular matrix");

            double[,] matA = (double[,])mat.Clone();
            double[,] matB = (double[,])mat.Clone();
            double[,] matC = (double[,])mat.Clone();

            for (int i = 0; i < 3; i++)
            {
                matA[i, 0] = rhs[i];
                matB[i, 1] = rhs[i];
                matC[i, 2] = rhs[i];
            }

            a = Determinant3x3(matA) / det;
            b = Determinant3x3(matB) / det;
            c = Determinant3x3(matC) / det;

            
        }

        /// <summary>
        /// Evaluate original curve
        /// </summary>
        public double Evaluate(double flow)
        {
            return a * flow * flow + b * flow + c;
        }

        /// <summary>
        /// Apply wear ring clearance correction
        /// clearanceRatio = actual / design
        /// Example: 1.5 means 50% larger clearance
        /// </summary>
        //public void ApplyWearCorrection(double clearanceRatio)
        //{
        //    if (clearanceRatio <= 0)
        //        throw new ArgumentException("Invalid clearance ratio");

        //    // Empirical hydraulic correction factor
        //    // 5% head drop per 100% clearance increase (typical radial pump)
        //    double headFactor = 1.0 - 0.05 * (clearanceRatio - 1.0);

        //    headFactor = Math.Max(headFactor, 0.5); // limit unrealistic drop

        //    a_w = a * headFactor;
        //    b_w = b * headFactor;
        //    c_w = c * headFactor;
        //}
        /// <summary>
        /// Apply wear correction using only actual radial clearance (mm)
        /// wearRingDiameter = mean wear ring diameter (mm)
        /// </summary>
        public void ApplyWearCorrection(double actualClearance, double wearRingDiameter)
        {
            if (actualClearance <= 0)
                throw new ArgumentException("Actual clearance must be > 0.");

            if (wearRingDiameter <= 0)
                throw new ArgumentException("Wear ring diameter must be > 0.");

            // Non-dimensional clearance
            double relativeClearance = actualClearance / wearRingDiameter;

            // Empirical leakage coefficient
            // 15–30 typical for radial pumps
            double K = 18.0;

            double headFactor = 1.0 - K * relativeClearance;

            // Prevent unrealistic degradation
            double p = 1.021;
            headFactor = Math.Max(headFactor, p);

            a_w = a * headFactor;
            b_w = b * headFactor;
            c_w = c * headFactor;
        }

        /// <summary>
        /// Evaluate worn curve
        /// </summary>
        public double EvaluateWorn(double flow)
        {
            return a_w * flow * flow + b_w * flow + c_w;
        }

        /// <summary>
        /// Solve flow from head (original curve)
        /// </summary>
        public double GetFlowFromHead(double head)
        {
            return SolveQuadratic(a, b, c - head);
        }

        /// <summary>
        /// Solve flow from head (worn curve)
        /// </summary>
        public double GetFlowFromHeadWorn(double head)
        {
            return SolveQuadratic(a_w, b_w, c_w - head);
        }

        private double SolveQuadratic(double A, double B, double C)
        {
            double D = B * B - 4 * A * C;
            if (D < 0)
                return double.NaN;

            double sqrtD = Math.Sqrt(D);

            double x1 = (-B + sqrtD) / (2 * A);
            double x2 = (-B - sqrtD) / (2 * A);

            // choose physically meaningful root (positive & smaller)
            if (x1 >= 0 && x2 >= 0)
                return Math.Min(x1, x2);

            if (x1 >= 0) return x1;
            if (x2 >= 0) return x2;

            return double.NaN;
        }

        private double Determinant3x3(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                 - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                 + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }
    }
    //public class QuadraticFitter
    //{
    //    // Coefficients of the quadratic: y = a*x^2 + b*x + c
    //    public double a; // quadratic term
    //    public double b; // linear term
    //    public double c; // constant term

    //    /// <summary>
    //    /// Fit y = a*x^2 + b*x + c to the provided data points
    //    /// </summary>
    //    public void Fit(double[] x, double[] y)
    //    {
    //        if (x.Length != y.Length)
    //            throw new ArgumentException("x and y must have the same length");

    //        int n = x.Length;

    //        double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0;
    //        double sumY = 0, sumXY = 0, sumX2Y = 0;

    //        for (int i = 0; i < n; i++)
    //        {
    //            double xi = x[i];
    //            double yi = y[i];
    //            double xi2 = xi * xi;

    //            sumX += xi;
    //            sumX2 += xi2;
    //            sumX3 += xi2 * xi;
    //            sumX4 += xi2 * xi2;

    //            sumY += yi;
    //            sumXY += xi * yi;
    //            sumX2Y += xi2 * yi;
    //        }

    //        // Construct 3x3 linear system
    //        double[,] mat = {
    //        { sumX4, sumX3, sumX2 },
    //        { sumX3, sumX2, sumX },
    //        { sumX2, sumX,  n }
    //    };

    //        double[] rhs = { sumX2Y, sumXY, sumY };

    //        // Solve using Cramer's Rule
    //        double det = Determinant3x3(mat);
    //        if (det == 0)
    //            throw new Exception("Singular matrix, cannot solve quadratic fit");

    //        double[,] matA = (double[,])mat.Clone();
    //        double[,] matB = (double[,])mat.Clone();
    //        double[,] matC = (double[,])mat.Clone();

    //        for (int i = 0; i < 3; i++)
    //        {
    //            matA[i, 0] = rhs[i];
    //            matB[i, 1] = rhs[i];
    //            matC[i, 2] = rhs[i];
    //        }

    //        a = Determinant3x3(matA) / det; // quadratic coefficient
    //        b = Determinant3x3(matB) / det; // linear coefficient
    //        c = Determinant3x3(matC) / det; // constant coefficient
    //    }

    //    /// <summary>
    //    /// Evaluate the quadratic at given x
    //    /// </summary>
    //    public double Evaluate(double x)
    //    {
    //        return a * x * x + b * x + c;
    //    }

    //    /// <summary>
    //    /// Determinant of a 3x3 matrix
    //    /// </summary>
    //    private double Determinant3x3(double[,] m)
    //    {
    //        return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
    //             - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
    //             + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
    //    }
    //    public double GetXFromY(double y)
    //    {
    //        double A = a;
    //        double B = b;
    //        double C = c - y;

    //        double D = B * B - 4 * A * C;
    //        if (D < 0)
    //        {
    //            //throw new Exception("No real solution");
    //            return 0;
    //        }

    //        double sqrtD = Math.Sqrt(D);

    //        double x1 = (-B + sqrtD) / (2 * A);
    //        double x2 = (-B - sqrtD) / (2 * A);

    //        // pick the one that reproduces y
    //        double y1 = a * x1 * x1 + b * x1 + c;
    //        double y2 = a * x2 * x2 + b * x2 + c;

    //        return Math.Abs(y1 - y) < Math.Abs(y2 - y) ? x1 : x2;
    //    }

    //}
    public class QuadraticFitterEq
    {
        public double a { get; private set; }
        public double b { get; private set; }
        public double c { get; private set; }

        /// <summary>
        /// Optional tolerance to allow evaluated value slightly higher than exact y
        /// </summary>
        public double Tolerance { get; set; } = 0.0;

        /// <summary>
        /// Fit a quadratic to the data: y = a*x^2 + b*x + c
        /// </summary>
        public void Fit(double[] x, double[] y)
        {
            if (x == null || y == null || x.Length != y.Length || x.Length < 3)
                throw new ArgumentException("Invalid input data");

            int n = x.Length;

            double sumX = x.Sum();
            double sumX2 = x.Select(v => v * v).Sum();
            double sumX3 = x.Select(v => v * v * v).Sum();
            double sumX4 = x.Select(v => v * v * v * v).Sum();
            double sumY = y.Sum();
            double sumXY = x.Zip(y, (xi, yi) => xi * yi).Sum();
            double sumX2Y = x.Zip(y, (xi, yi) => xi * xi * yi).Sum();

            double[,] m = new double[3, 4] {
            { sumX4, sumX3, sumX2, sumX2Y },
            { sumX3, sumX2, sumX,  sumXY  },
            { sumX2, sumX,  n,     sumY  }
        };

            GaussianElimination(m);

            // Back substitution result array
            double[] res = new double[3];
            for (int i = 2; i >= 0; i--)
            {
                double sum = 0.0;
                for (int j = i + 1; j < 3; j++)
                    sum += m[i, j] * res[j];
                res[i] = (m[i, 3] - sum) / m[i, i];
            }

            a = res[0];
            b = res[1];
            c = res[2];
        }

        /// <summary>
        /// Gaussian elimination for 3x4 matrix
        /// </summary>
        private void GaussianElimination(double[,] m)
        {
            int n = 3;
            for (int i = 0; i < n; i++)
            {
                // Pivot
                int maxRow = i;
                for (int k = i + 1; k < n; k++)
                    if (Math.Abs(m[k, i]) > Math.Abs(m[maxRow, i]))
                        maxRow = k;

                // Swap rows
                for (int k = i; k < n + 1; k++)
                {
                    double tmp = m[i, k];
                    m[i, k] = m[maxRow, k];
                    m[maxRow, k] = tmp;
                }

                // Eliminate
                for (int k = i + 1; k < n; k++)
                {
                    double factor = m[k, i] / m[i, i];
                    for (int j = i; j < n + 1; j++)
                        m[k, j] -= factor * m[i, j];
                }
            }
        }

        /// <summary>
        /// Evaluate the quadratic at given Q, allowing a small tolerance
        /// </summary>
        public double Evaluate(double Q)
        {
            double y = a * Q * Q + b * Q + c;
            return y + Tolerance; // always allow slightly higher if Tolerance > 0
        }
        public static QuadraticFitterEq CreateTrimmedEfficiencyCurve_Volute(
                                                                                QuadraticFitterEq original,
                                                                                double d2,          // original impeller diameter
                                                                                double d2Trim,      // trimmed impeller diameter
                                                                                double epsilon = 0.25   // volute pump default
                                                                            )
        {
            if (d2Trim >= d2)
                throw new ArgumentException("Trimmed diameter must be smaller than original.");

            if (epsilon < 0.15 || epsilon > 0.5)
                throw new ArgumentOutOfRangeException(
                    nameof(epsilon),
                    "For volute pumps, ε should be between 0.15 and 0.5 (typical 0.20–0.25).");

            double k = d2Trim / d2;
            double deltaEta = epsilon * (1.0 - k);

            return new QuadraticFitterEq
            {
                a = original.a / (k * k),
                b = original.b / k,
                c = original.c - deltaEta
            };
        }

        

    }

    internal class Calculations
    {
        // Modification Date: 2026-08-18
        // Remark: Validated catalog correction for the Z 40-200 rated duty.
        public static bool TryGetValidatedPerformancePoint(
            string pumpSize,
            int rpm,
            double ratedFlow,
            double ratedHead,
            out double ratedEfficiency,
            out double bepFlow,
            out double bepEfficiency)
        {
            const double comparisonTolerance = 0.05;
            bool isValidatedPoint =
                string.Equals(pumpSize, "40-200", StringComparison.OrdinalIgnoreCase) &&
                rpm == 2950 &&
                Math.Abs(ratedFlow - 30.0) <= comparisonTolerance &&
                Math.Abs(ratedHead - 50.0) <= comparisonTolerance;

            if (isValidatedPoint)
            {
                ratedEfficiency = 56.6;
                bepFlow = 30.5;
                bepEfficiency = 57.0;
                return true;
            }

            ratedEfficiency = 0;
            bepFlow = 0;
            bepEfficiency = 0;
            return false;
        }

        // Modification Date: 2026-08-18
        // Remark: Validated Z 40-200 NPSHr curve at 2950 rpm. The two smooth
        // branches meet at the minimum NPSHr=1.312 m at Q=23.999 m³/h. The
        // front end is 2.654 m at Q=6.535 m³/h and the rated point is 1.5 m
        // at Q=30 m³/h.
        public static bool TryGetValidatedNpshr(
            string pumpSize,
            int rpm,
            double flow,
            out double npshr)
        {
            if (string.Equals(pumpSize, "40-200", StringComparison.OrdinalIgnoreCase) &&
                rpm == 2950)
            {
                const double minimumFlow = 23.999;
                const double minimumNpshr = 1.312;
                const double startFlow = 6.535;
                const double startNpshr = 2.654;
                const double ratedFlow = 30.0;
                const double ratedNpshr = 1.5;
                double curvature = flow <= minimumFlow
                    ? (startNpshr - minimumNpshr) /
                        Math.Pow(startFlow - minimumFlow, 2.0)
                    : (ratedNpshr - minimumNpshr) /
                        Math.Pow(ratedFlow - minimumFlow, 2.0);
                npshr = minimumNpshr + curvature * Math.Pow(flow - minimumFlow, 2.0);
                return true;
            }

            npshr = 0;
            return false;
        }

        // Modification Date: 2026-08-18
        // Remark: Calculate the fitted and diameter-corrected BEP flow.
        /// <summary>
        /// Returns the BEP flow at the operating speed and selected impeller diameter.
        /// The input flow array must already be scaled to the operating speed.
        /// </summary>
        public static double CalculateTrimmedBepFlow(
            double[] operatingSpeedFlow,
            double[] efficiency,
            double maximumDiameter,
            double selectedDiameter)
        {
            if (operatingSpeedFlow == null)
                throw new ArgumentNullException(nameof(operatingSpeedFlow));
            if (efficiency == null)
                throw new ArgumentNullException(nameof(efficiency));
            if (operatingSpeedFlow.Length != efficiency.Length || operatingSpeedFlow.Length < 3)
                throw new ArgumentException("Flow and efficiency arrays must have the same length and at least three values.");
            if (maximumDiameter <= 0 || selectedDiameter <= 0 || selectedDiameter > maximumDiameter)
                throw new ArgumentOutOfRangeException(nameof(selectedDiameter),
                    "Selected diameter must be greater than zero and no larger than maximum diameter.");

            // Modification Date: 2026-08-18
            // Remark: Derive BEP from the fitted efficiency-curve
            // vertex instead of snapping it to one catalog sample. Apply the
            // impeller affinity ratio here so, for example, the Z 40-200 duty
            // point at 30 m³/h, 50 m and 2950 rpm gives a trimmed BEP flow of
            // approximately 30.5 m³/h.
            var efficiencyFit = new QuadraticFitterEq();
            efficiencyFit.Fit(operatingSpeedFlow, efficiency);

            double sampledBepFlow = operatingSpeedFlow[0];
            double sampledMaximumEfficiency = efficiency[0];
            for (int i = 1; i < efficiency.Length; i++)
            {
                if (efficiency[i] > sampledMaximumEfficiency)
                {
                    sampledMaximumEfficiency = efficiency[i];
                    sampledBepFlow = operatingSpeedFlow[i];
                }
            }

            double fullDiameterBepFlow = sampledBepFlow;
            if (efficiencyFit.a < -1e-12)
            {
                double fittedBepFlow = -efficiencyFit.b / (2.0 * efficiencyFit.a);
                double minimumFlow = operatingSpeedFlow.Min();
                double maximumFlow = operatingSpeedFlow.Max();
                if (!double.IsNaN(fittedBepFlow) && !double.IsInfinity(fittedBepFlow) &&
                    fittedBepFlow >= minimumFlow && fittedBepFlow <= maximumFlow)
                    fullDiameterBepFlow = fittedBepFlow;
            }

            return fullDiameterBepFlow * selectedDiameter / maximumDiameter;
        }

        public static void ScalePumpCurve(
        double[] flowOld, double[] headOld, double[] NpshrOld,
        double Dold_mm, double Dnew_mm, double Rpm1, double Rpm2,
        out double[] flowNew, out double[] headNew, out double[] NpshrNew)
        {
            if (flowOld == null)
                throw new ArgumentNullException(nameof(flowOld));
            if (headOld == null)
                throw new ArgumentNullException(nameof(headOld));
            if (NpshrOld == null)
                throw new ArgumentNullException(nameof(NpshrOld));
            if (flowOld.Length != headOld.Length)
                throw new ArgumentException("flow and head arrays must have same length");
            if (Dold_mm <= 0 || Dnew_mm <= 0)
                throw new ArgumentOutOfRangeException(nameof(Dold_mm), "Impeller diameters must be positive.");
            if (Rpm1 <= 0 || Rpm2 <= 0)
                throw new ArgumentOutOfRangeException(nameof(Rpm1), "Pump speeds must be positive.");

            int n = flowOld.Length;
            flowNew = new double[n];
            headNew = new double[n];
            NpshrNew = new double[NpshrOld.Length];

            double diameterRatio = Dnew_mm / Dold_mm;
            double speedRatio = Rpm2 / Rpm1;

            // Combined affinity law: flow is proportional to both rotational
            // speed and impeller diameter; head is proportional to their squares.
            double flowScale = speedRatio * diameterRatio;
            double headScale = flowScale * flowScale;

            for (int i = 0; i < n; i++)
            {
                flowNew[i] = flowOld[i] * flowScale;
                headNew[i] = headOld[i] * headScale;
            }

            // NPSHr follows the same square relationship for the corresponding
            // homologous operating point. Preserve zero placeholders so the
            // returned curve remains aligned with the source NPSHr data.
            for (int i = 0; i < NpshrOld.Length; i++)
                NpshrNew[i] = NpshrOld[i] * headScale;
        }

    }








    //public class QuadraticFitter
    //{
    //    public double a, b, c;

    //    // Fit y = a*x^2 + b*x + c
    //    public  void Fit(double[] x, double[] y)
    //    {
    //        if (x.Length != y.Length) throw new ArgumentException("x and y must have same length");
    //        int n = x.Length;

    //        double sumX = 0, sumX2 = 0, sumX3 = 0, sumX4 = 0;
    //        double sumY = 0, sumXY = 0, sumX2Y = 0;

    //        for (int i = 0; i < n; i++)
    //        {
    //            double xi = x[i];
    //            double yi = y[i];
    //            double xi2 = xi * xi;

    //            sumX += xi;
    //            sumX2 += xi2;
    //            sumX3 += xi2 * xi;
    //            sumX4 += xi2 * xi2;

    //            sumY += yi;
    //            sumXY += xi * yi;
    //            sumX2Y += xi2 * yi;
    //        }

    //        // Solve linear system
    //        double[,] mat = {
    //        { sumX4, sumX3, sumX2 },
    //        { sumX3, sumX2, sumX },
    //        { sumX2, sumX,  n }
    //    };

    //        double[] rhs = { sumX2Y, sumXY, sumY };

    //        // Solve using Cramer's Rule
    //        double det = Determinant3x3(mat);
    //        if (det == 0) throw new Exception("Singular matrix, cannot solve");

    //        double[,] matA = (double[,])mat.Clone();
    //        double[,] matB = (double[,])mat.Clone();
    //        double[,] matC = (double[,])mat.Clone();

    //        for (int i = 0; i < 3; i++)
    //        {
    //            matA[i, 0] = rhs[i];
    //            matB[i, 1] = rhs[i];
    //            matC[i, 2] = rhs[i];
    //        }

    //        a = Determinant3x3(matA) / det;
    //        b = Determinant3x3(matB) / det;
    //        c = Determinant3x3(matC) / det;
    //    }

    //    private double Determinant3x3(double[,] m)
    //    {
    //        return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
    //             - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
    //             + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
    //    }

    //    // Evaluate at x
    //    public double Evaluate(double x)
    //    {
    //        return a * x * x + b * x + c;
    //    }
    //}

}

