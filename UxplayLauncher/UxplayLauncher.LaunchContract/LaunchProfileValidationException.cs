namespace UxplayLauncher.LaunchContract;

public sealed class LaunchProfileValidationException : ArgumentException
{
  internal LaunchProfileValidationException(IReadOnlyList<ContractViolation> violations)
      : base(CreateMessage(violations))
  {
    Violations = violations;
  }

  public IReadOnlyList<ContractViolation> Violations { get; }

  private static string CreateMessage(IReadOnlyList<ContractViolation> violations)
  {
    return $"Launch profile validation failed: {string.Join("; ", violations)}";
  }
}
