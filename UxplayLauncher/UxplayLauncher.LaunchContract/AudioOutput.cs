namespace UxplayLauncher.LaunchContract;

public abstract class AudioOutput
{
  private AudioOutput()
  {
  }

  public sealed class Disabled : AudioOutput
  {
  }

  public sealed class Sink : AudioOutput
  {
    public Sink(string name)
    {
      Name = name;
    }

    public string Name { get; }
  }
}
