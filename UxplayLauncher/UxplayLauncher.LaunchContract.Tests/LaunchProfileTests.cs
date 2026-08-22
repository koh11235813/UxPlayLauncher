using UxplayLauncher.LaunchContract;
using Xunit;

namespace UxplayLauncher.LaunchContract.Tests;

public sealed class LaunchProfileTests
{
  [Fact]
  public void DefaultProfileUsesV1736Values()
  {
    LaunchProfile profile = LaunchProfile.CreateDefault();

    Assert.Equal("1920x1080", profile.Resolution);
    Assert.Equal(30, profile.MaxFps);
    Assert.Equal("UxPlay-Windows", profile.DeviceName);
    Assert.False(profile.EnableHls);
    Assert.False(profile.AsyncAudio);
    Assert.False(profile.LowLatencyMirror);
    AudioOutput.Sink sink = Assert.IsType<AudioOutput.Sink>(profile.AudioOutput);
    Assert.Equal("directsoundsink", sink.Name);
    Assert.Null(profile.AudioLatencySeconds);
    Assert.Null(profile.Password);
    Assert.Null(profile.BasePort);
    Assert.Equal("d3d11videosink fullscreen-toggle-mode=alt-enter", profile.VideoSink);
    Assert.False(profile.EnableUxPlayDebug);
    Assert.Null(profile.Recording);
  }

  [Fact]
  public void AcceptedValuesRemainUnchanged()
  {
    var recording = new Mp4Recording("capture.mp4");
    var audioOutput = new AudioOutput.Sink("custom sink");

    var profile = new LaunchProfile(
        "1280x720@60",
        60,
        "Living Room",
        true,
        true,
        true,
        audioOutput,
        0.25m,
        "パスワード六文字",
        1024,
        "d3d11videosink",
        true,
        recording);

    Assert.Equal("1280x720@60", profile.Resolution);
    Assert.Equal(60, profile.MaxFps);
    Assert.Equal("Living Room", profile.DeviceName);
    Assert.True(profile.EnableHls);
    Assert.True(profile.AsyncAudio);
    Assert.True(profile.LowLatencyMirror);
    Assert.Same(audioOutput, profile.AudioOutput);
    Assert.Equal(0.25m, profile.AudioLatencySeconds);
    AssertSecretEquals("パスワード六文字", profile.Password);
    Assert.Equal(1024, profile.BasePort);
    Assert.Equal("d3d11videosink", profile.VideoSink);
    Assert.True(profile.EnableUxPlayDebug);
    Assert.Same(recording, profile.Recording);
  }

  [Theory]
  [InlineData("0x1080")]
  [InlineData("10000x1080")]
  [InlineData("1920x0")]
  [InlineData("1920x1080@0")]
  [InlineData("1920x1080@256")]
  [InlineData("１９２０x１０８０")]
  [InlineData("1920/1080")]
  public void InvalidResolutionIsRejected(string resolution)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(resolution: resolution));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "resolution" && violation.ReasonCode == "invalid-format");
  }

  [Theory]
  [InlineData(0)]
  [InlineData(256)]
  public void FpsOutsideRangeIsRejected(int maxFps)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(maxFps: maxFps));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "maxFps" && violation.ReasonCode == "out-of-range");
  }

  [Theory]
  [InlineData(1023)]
  [InlineData(65534)]
  [InlineData(65535)]
  public void BasePortWithoutThreePortsIsRejected(int basePort)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(basePort: basePort));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "basePort" && violation.ReasonCode == "out-of-range");
  }

  [Theory]
  [InlineData(-0.01)]
  [InlineData(10.01)]
  public void AudioLatencyOutsideRangeIsRejected(double latency)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(audioLatencySeconds: (decimal)latency));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "audioLatencySeconds" && violation.ReasonCode == "out-of-range");
  }

  [Fact]
  public void AudioLatencyBoundsAreAccepted()
  {
    Assert.Equal(0m, CreateProfile(audioLatencySeconds: 0m).AudioLatencySeconds);
    Assert.Equal(10m, CreateProfile(audioLatencySeconds: 10m).AudioLatencySeconds);
  }

  [Fact]
  public void NumericBoundsAreAccepted()
  {
    Assert.Equal("1x1@1", CreateProfile(resolution: "1x1@1", maxFps: 1, basePort: 1024).Resolution);

    LaunchProfile maximums = CreateProfile(
        resolution: "9999x9999@255",
        maxFps: 255,
        basePort: 65533);

    Assert.Equal("9999x9999@255", maximums.Resolution);
    Assert.Equal(255, maximums.MaxFps);
    Assert.Equal(65533, maximums.BasePort);
  }

  [Fact]
  public void DisabledAudioWithoutSinkIsAccepted()
  {
    Assert.IsType<AudioOutput.Disabled>(CreateProfile(audioOutput: new AudioOutput.Disabled()).AudioOutput);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void MissingPasswordBecomesUnspecified(string? password)
  {
    Assert.Null(CreateProfile(password: password).Password);
  }

  [Fact]
  public void PasswordLengthUsesUnicodeScalars()
  {
    string password = "😀😀😀😀😀😀";

    AssertSecretEquals(password, CreateProfile(password: password).Password);
  }

  [Theory]
  [InlineData("12345")]
  [InlineData("😀😀😀😀😀")]
  public void ShortPasswordIsRejected(string password)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(password: password));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "password" && violation.ReasonCode == "too-short");
  }

  [Fact]
  public void NulIsRejectedInArgumentsAndPaths()
  {
    AssertInvalidArgvAndPathString("bad\0value");
  }

  [Fact]
  public void UnpairedHighSurrogateIsRejected()
  {
    AssertInvalidArgvAndPathString("bad\ud800value");
  }

  [Fact]
  public void UnpairedLowSurrogateIsRejected()
  {
    AssertInvalidArgvAndPathString("bad\udfffvalue");
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void BlankDeviceNameIsRejected(string deviceName)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(deviceName: deviceName));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "deviceName" && violation.ReasonCode == "required");
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void BlankAudioSinkIsRejected(string sink)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(audioOutput: new AudioOutput.Sink(sink)));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "audioOutput" && violation.ReasonCode == "required");
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void BlankVideoSinkIsRejected(string sink)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(videoSink: sink));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "videoSink" && violation.ReasonCode == "required");
  }

  [Fact]
  public void MissingRequiredValuesAreAggregated()
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        new LaunchProfile(
            null!,
            30,
            null!,
            enableHls: false,
            asyncAudio: false,
            lowLatencyMirror: false,
            null!,
            audioLatencySeconds: null,
            password: null,
            basePort: null,
            null!,
            enableUxPlayDebug: false,
            recording: null));

    Assert.Equal(
        new[] { "resolution", "deviceName", "audioOutput", "videoSink" },
        exception.Violations.Select(violation => violation.Field));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData(".")]
  [InlineData("..")]
  [InlineData("captures/../capture.mp4")]
  [InlineData("captures\\..\\capture.mp4")]
  [InlineData("/tmp/capture.mp4")]
  [InlineData("\\tmp\\capture.mp4")]
  [InlineData("C:capture.mp4")]
  [InlineData("C:\\capture.mp4")]
  [InlineData("//server/share/capture.mp4")]
  [InlineData("\\\\server\\share\\capture.mp4")]
  public void EscapingRecordingPathIsRejected(string fileName)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(recording: new Mp4Recording(fileName)));

    Assert.Contains(exception.Violations, violation =>
        violation.Field == "recording" && violation.ReasonCode == "invalid-path");
  }

  [Fact]
  public void PlainRecordingNameIsAccepted()
  {
    Assert.Equal("capture.mp4", CreateProfile(recording: new Mp4Recording("capture.mp4")).Recording?.FileName);
  }

  [Fact]
  public void FlagWhitespaceAndQuotesArePreserved()
  {
    const string deviceName = " \"Living Room\" ";
    const string audioSink = " custom audio sink ";
    const string videoSink = " \"custom video sink\" ";

    LaunchProfile profile = CreateProfile(
        deviceName: deviceName,
        audioOutput: new AudioOutput.Sink(audioSink),
        videoSink: videoSink);

    Assert.Equal(deviceName, profile.DeviceName);
    Assert.Equal(audioSink, Assert.IsType<AudioOutput.Sink>(profile.AudioOutput).Name);
    Assert.Equal(videoSink, profile.VideoSink);
  }

  [Fact]
  public void NullRecordingNameKeepsRecordingEnabled()
  {
    var recording = new Mp4Recording(null);

    LaunchProfile profile = CreateProfile(recording: recording);

    Mp4Recording enabledRecording = Assert.IsType<Mp4Recording>(profile.Recording);
    Assert.Same(recording, enabledRecording);
    Assert.Null(enabledRecording.FileName);
  }

  [Fact]
  public void NestedRecordingNameIsAccepted()
  {
    const string fileName = "captures/session 1/capture.mp4";

    Assert.Equal(fileName, CreateProfile(recording: new Mp4Recording(fileName)).Recording?.FileName);
  }

  [Fact]
  public void ViolationsHaveStableOrderAndHidePassword()
  {
    const string secret = "秘密abc";

    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(
            resolution: "0x0",
            maxFps: 0,
            deviceName: " ",
            audioOutput: new AudioOutput.Sink(" "),
            audioLatencySeconds: -1m,
            password: secret,
            basePort: 65535,
            videoSink: " ",
            recording: new Mp4Recording("../capture.mp4")));

    Assert.Equal(
        new[] { "resolution", "maxFps", "deviceName", "audioOutput", "audioLatencySeconds", "password", "basePort", "videoSink", "recording" },
        exception.Violations.Select(violation => violation.Field));
    Assert.False(exception.Message.Contains(secret, StringComparison.Ordinal));
    Assert.False(exception.ToString().Contains(secret, StringComparison.Ordinal));
    Assert.False(string.Join("|", exception.Violations.Select(violation => violation.ToString())).Contains(secret, StringComparison.Ordinal));
    Assert.False(string.Join("|", exception.Violations.Select(violation => violation.ValueSummary)).Contains(secret, StringComparison.Ordinal));
    Assert.Contains(exception.Violations, violation =>
        violation.Field == "password" && violation.ReasonCode == "too-short");
  }

  [Fact]
  public void AudioOutputCannotBeExtendedExternally()
  {
    Assert.True(typeof(AudioOutput).IsAbstract);
    Assert.True(typeof(AudioOutput.Disabled).IsSealed);
    Assert.True(typeof(AudioOutput.Sink).IsSealed);
    System.Reflection.ConstructorInfo constructor = Assert.Single(
        typeof(AudioOutput).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    Assert.True(constructor.IsPrivate);
  }

  private static LaunchProfile CreateProfile(
      string resolution = "1920x1080",
      int maxFps = 30,
      string deviceName = "UxPlay-Windows",
      AudioOutput? audioOutput = null,
      decimal? audioLatencySeconds = null,
      string? password = null,
      int? basePort = null,
      string videoSink = "d3d11videosink fullscreen-toggle-mode=alt-enter",
      Mp4Recording? recording = null)
  {
    return new LaunchProfile(
        resolution,
        maxFps,
        deviceName,
        enableHls: false,
        asyncAudio: false,
        lowLatencyMirror: false,
        audioOutput ?? new AudioOutput.Sink("directsoundsink"),
        audioLatencySeconds,
        password,
        basePort,
        videoSink,
        enableUxPlayDebug: false,
        recording);
  }

  private static void AssertInvalidArgvAndPathString(string value)
  {
    LaunchProfileValidationException exception = Assert.Throws<LaunchProfileValidationException>(() =>
        CreateProfile(
            resolution: value,
            deviceName: value,
            audioOutput: new AudioOutput.Sink(value),
            password: value,
            videoSink: value,
            recording: new Mp4Recording(value)));

    Assert.Equal(
        new[] { "resolution", "deviceName", "audioOutput", "password", "videoSink", "recording" },
        exception.Violations.Select(violation => violation.Field));
    Assert.All(exception.Violations, violation => Assert.Equal("invalid-characters", violation.ReasonCode));
  }

  private static void AssertSecretEquals(string? expected, string? actual)
  {
    bool equal = expected is null
        ? actual is null
        : actual is not null && expected.AsSpan().SequenceEqual(actual.AsSpan());

    Assert.True(equal);
  }
}
