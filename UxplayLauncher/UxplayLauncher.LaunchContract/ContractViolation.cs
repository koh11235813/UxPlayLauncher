namespace UxplayLauncher.LaunchContract;

public sealed class ContractViolation
{
  internal ContractViolation(string field, string reasonCode, string valueSummary)
  {
    Field = field;
    ReasonCode = reasonCode;
    ValueSummary = valueSummary;
  }

  public string Field { get; }

  public string ReasonCode { get; }

  public string ValueSummary { get; }

  public override string ToString() => $"{Field}: {ReasonCode} ({ValueSummary})";
}
