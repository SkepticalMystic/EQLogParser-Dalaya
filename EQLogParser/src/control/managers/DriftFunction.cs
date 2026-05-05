namespace EQLogParser
{
  // Linear drift correction for one source's clock vs the anchor's clock within a single fight
  // cluster. EQ logs from different players show clock drift that grows over the course of a
  // single boss fight (network jitter on top of small clock skew); a constant offset is not
  // enough to align them. This represents:
  //
  //   drift(t) = Intercept + Slope * (t - T0)
  //
  // where t is an offset-adjusted timestamp from the source being corrected, and the predicted
  // drift is subtracted from t to bring it into the merged frame. Fit per cluster from paired
  // damage record observations against the anchor source.
  internal class DriftFunction
  {
    public double Intercept { get; }
    public double Slope { get; }
    public double T0 { get; }

    internal DriftFunction(double intercept, double slope, double t0)
    {
      Intercept = intercept;
      Slope = slope;
      T0 = t0;
    }

    // Returns the predicted drift between the source clock and the anchor clock at time t.
    internal double Predict(double t) => Intercept + Slope * (t - T0);

    // Returns t corrected into the merged frame: subtracts predicted drift.
    internal double Correct(double t) => t - Predict(t);
  }
}
