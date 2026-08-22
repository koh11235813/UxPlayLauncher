using System.Globalization;

namespace UxplayLauncher.LaunchContract;

public sealed class LaunchProfile
{
  public LaunchProfile(
      string resolution,
      int maxFps,
      string deviceName,
      bool enableHls,
      bool asyncAudio,
      bool lowLatencyMirror,
      AudioOutput audioOutput,
      decimal? audioLatencySeconds,
      string? password,
      int? basePort,
      string videoSink,
      bool enableUxPlayDebug,
      Mp4Recording? recording)
  {
    var violations = new List<ContractViolation>();

    ValidateResolution(resolution, violations);
    ValidateRange("maxFps", maxFps, 1, 255, violations);
    ValidateRequiredText("deviceName", deviceName, violations);
    ValidateAudioOutput(audioOutput, violations);
    ValidateRange("audioLatencySeconds", audioLatencySeconds, 0m, 10m, violations);

    string? normalizedPassword = string.IsNullOrEmpty(password) ? null : password;
    ValidatePassword(normalizedPassword, violations);

    ValidateRange("basePort", basePort, 1024, 65533, violations);
    ValidateRequiredText("videoSink", videoSink, violations);
    ValidateRecording(recording, violations);

    if (violations.Count > 0)
    {
      throw new LaunchProfileValidationException(violations.AsReadOnly());
    }

    Resolution = resolution;
    MaxFps = maxFps;
    DeviceName = deviceName;
    EnableHls = enableHls;
    AsyncAudio = asyncAudio;
    LowLatencyMirror = lowLatencyMirror;
    AudioOutput = audioOutput;
    AudioLatencySeconds = audioLatencySeconds;
    Password = normalizedPassword;
    BasePort = basePort;
    VideoSink = videoSink;
    EnableUxPlayDebug = enableUxPlayDebug;
    Recording = recording;
  }

  public string Resolution { get; }

  public int MaxFps { get; }

  public string DeviceName { get; }

  public bool EnableHls { get; }

  public bool AsyncAudio { get; }

  public bool LowLatencyMirror { get; }

  public AudioOutput AudioOutput { get; }

  public decimal? AudioLatencySeconds { get; }

  internal string? Password { get; }

  public int? BasePort { get; }

  public string VideoSink { get; }

  public bool EnableUxPlayDebug { get; }

  public Mp4Recording? Recording { get; }

  public static LaunchProfile CreateDefault()
  {
    return new LaunchProfile(
        "1920x1080",
        30,
        "UxPlay-Windows",
        enableHls: false,
        asyncAudio: false,
        lowLatencyMirror: false,
        new AudioOutput.Sink("directsoundsink"),
        audioLatencySeconds: null,
        password: null,
        basePort: null,
        "d3d11videosink fullscreen-toggle-mode=alt-enter",
        enableUxPlayDebug: false,
        recording: null);
  }

  private static void ValidateResolution(string? value, ICollection<ContractViolation> violations)
  {
    if (ContainsInvalidCharacters(value))
    {
      violations.Add(CreateViolation("resolution", "invalid-characters", value));
      return;
    }

    if (!TryParseResolution(value, out _, out _, out _))
    {
      violations.Add(CreateViolation("resolution", "invalid-format", value));
    }
  }

  private static bool TryParseResolution(string? value, out int width, out int height, out int? refresh)
  {
    width = 0;
    height = 0;
    refresh = null;

    if (string.IsNullOrEmpty(value))
    {
      return false;
    }

    int xIndex = value.IndexOf('x');
    if (xIndex <= 0 || xIndex == value.Length - 1 || value.IndexOf('x', xIndex + 1) >= 0)
    {
      return false;
    }

    string widthText = value[..xIndex];
    string remainder = value[(xIndex + 1)..];
    int atIndex = remainder.IndexOf('@');
    string heightText = atIndex >= 0 ? remainder[..atIndex] : remainder;
    string? refreshText = atIndex >= 0 ? remainder[(atIndex + 1)..] : null;

    if (!TryParseAsciiUnsigned(widthText, out width) ||
        !TryParseAsciiUnsigned(heightText, out height))
    {
      return false;
    }

    if (refreshText is not null)
    {
      if (!TryParseAsciiUnsigned(refreshText, out int parsedRefresh))
      {
        return false;
      }

      refresh = parsedRefresh;
    }

    return width is >= 1 and <= 9999 &&
           height is >= 1 and <= 9999 &&
           (refresh is null or (>= 1 and <= 255));
  }

  private static bool TryParseAsciiUnsigned(string value, out int parsed)
  {
    parsed = 0;
    if (value.Length == 0)
    {
      return false;
    }

    foreach (char character in value)
    {
      if (character is < '0' or > '9')
      {
        return false;
      }

      parsed = (parsed * 10) + (character - '0');
      if (parsed > 9999)
      {
        return false;
      }
    }

    return true;
  }

  private static void ValidateRequiredText(string field, string? value, ICollection<ContractViolation> violations)
  {
    if (ContainsInvalidCharacters(value))
    {
      violations.Add(CreateViolation(field, "invalid-characters", value));
      return;
    }

    if (string.IsNullOrWhiteSpace(value))
    {
      violations.Add(CreateViolation(field, "required", value));
    }
  }

  private static void ValidateAudioOutput(AudioOutput? value, ICollection<ContractViolation> violations)
  {
    if (value is null)
    {
      violations.Add(CreateViolation("audioOutput", "required", value));
      return;
    }

    if (value is AudioOutput.Sink sink)
    {
      ValidateRequiredText("audioOutput", sink.Name, violations);
    }
  }

  private static void ValidatePassword(string? value, ICollection<ContractViolation> violations)
  {
    if (value is null)
    {
      return;
    }

    if (ContainsInvalidCharacters(value))
    {
      violations.Add(CreateViolation("password", "invalid-characters", value));
      return;
    }

    if (CountUnicodeScalars(value) < 6)
    {
      violations.Add(CreateViolation("password", "too-short", value));
    }
  }

  private static void ValidateRecording(Mp4Recording? recording, ICollection<ContractViolation> violations)
  {
    string? fileName = recording?.FileName;
    if (fileName is null)
    {
      return;
    }

    if (ContainsInvalidCharacters(fileName))
    {
      violations.Add(CreateViolation("recording", "invalid-characters", fileName));
      return;
    }

    if (string.IsNullOrWhiteSpace(fileName) || IsWorkspaceEscapingPath(fileName))
    {
      violations.Add(CreateViolation("recording", "invalid-path", fileName));
    }
  }

  private static bool IsWorkspaceEscapingPath(string value)
  {
    if (value[0] is '/' or '\\' ||
        (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') ||
        (value.Length >= 2 && value[0] is '/' or '\\' && value[1] == value[0]))
    {
      return true;
    }

    string[] segments = value.Split(new[] { '/', '\\' }, StringSplitOptions.None);
    return segments.Any(segment => segment is "." or "..");
  }

  private static void ValidateRange<T>(string field, T? value, T minimum, T maximum, ICollection<ContractViolation> violations)
      where T : struct, IComparable<T>
  {
    if (value.HasValue && (value.Value.CompareTo(minimum) < 0 || value.Value.CompareTo(maximum) > 0))
    {
      violations.Add(CreateViolation(field, "out-of-range", value));
    }
  }

  private static bool ContainsInvalidCharacters(string? value)
  {
    if (value is null)
    {
      return false;
    }

    for (int index = 0; index < value.Length; index++)
    {
      char character = value[index];
      if (character == '\0')
      {
        return true;
      }

      if (char.IsHighSurrogate(character))
      {
        if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
        {
          return true;
        }

        index++;
      }
      else if (char.IsLowSurrogate(character))
      {
        return true;
      }
    }

    return false;
  }

  private static int CountUnicodeScalars(string value)
  {
    int count = 0;
    for (int index = 0; index < value.Length; index++, count++)
    {
      if (char.IsHighSurrogate(value[index]))
      {
        index++;
      }
    }

    return count;
  }

  private static ContractViolation CreateViolation(string field, string reasonCode, object? value)
  {
    return new ContractViolation(field, reasonCode, CreateValueSummary(field, value));
  }

  private static string CreateValueSummary(string field, object? value)
  {
    if (field == "password")
    {
      return value is null ? "not-specified" : "provided";
    }

    if (value is null)
    {
      return "not-specified";
    }

    if (value is string text)
    {
      return text.Length switch
      {
        0 => "empty",
        _ when string.IsNullOrWhiteSpace(text) => "whitespace",
        _ => $"provided(length={text.Length.ToString(CultureInfo.InvariantCulture)})"
      };
    }

    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "provided";
  }
}
