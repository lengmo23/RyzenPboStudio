namespace RyzenPboStudio;

/// <summary>Removes frequency differences from single-core voltages. Each core boosts to its own FMAX and the SMU
/// follows the F/V curve, so a faster core legitimately asks for more voltage; comparing raw VID would rank
/// well-binned fast cores as voltage-hungry.</summary>
internal static class FvAnalysis
{
    /// <summary>Typical Zen4 / Zen5 F/V slope near 5.5 GHz, used when the samples cannot give a sane fit.</summary>
    public const double DefaultSlope = 0.00049;   // V/MHz

    private const double SlopeMin = 0.0001;       // 0.1 mV/MHz
    private const double SlopeMax = 0.0015;       // 1.5 mV/MHz

    /// <summary>SVI3 quantisation step; voltage differences below this are noise.</summary>
    public const double NoiseFloor = 0.005;

    /// <summary>One least-squares fit over all CCDs after centring each CCD on its own mean, which cancels the
    /// die-to-die bin offset while keeping more samples than per-CCD fits. Falls back to DefaultSlope when off-range.</summary>
    public static double Slope(IReadOnlyList<ScbRow> rows, out bool fallback)
    {
        double sxy = 0, sxx = 0;
        foreach (var g in rows.Where(r => r.Valid).GroupBy(r => r.Ccd))
        {
            var g2 = g.ToList();
            if (g2.Count < 2) continue;
            double mf = g2.Average(r => r.Freq), mv = g2.Average(r => r.AlignV);
            foreach (var r in g2)
            {
                double df = r.Freq - mf, dv = r.AlignV - mv;
                sxy += df * dv;
                sxx += df * df;
            }
        }

        double s = sxx > 0 ? sxy / sxx : 0;
        fallback = s < SlopeMin || s > SlopeMax;
        return fallback ? DefaultSlope : s;
    }

    /// <summary>Voltage of <paramref name="r"/> above <paramref name="target"/> at the target's frequency (V).
    /// Equals the residual difference of the two cores; with equal frequencies it is just the voltage difference.</summary>
    public static double DeltaV(ScbRow r, ScbRow target, double slope) =>
        (r.AlignV - target.AlignV) - slope * (r.Freq - target.Freq);
}
