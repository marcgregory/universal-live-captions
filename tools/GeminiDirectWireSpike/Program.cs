using UniversalCaptions.Speech.Gemini.Tests.Spikes;

string[] args0 = args.Length > 0 && args[0] == "--" ? args[1..] : args;

if (args0.Contains("--probe", StringComparer.Ordinal))
{
    int exitCode = await GeminiResampleProbe.RunAsync(args0);
    return exitCode;
}

int exitCode0 = await GeminiDirectWireSpike.RunAsync(args0);
return exitCode0;