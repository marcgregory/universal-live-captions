using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Core.Processing;

/// <summary>
/// Transforms audio chunks into a target <see cref="OutputFormat"/> (for example, down-mixing
/// channels and resampling to the rate expected by the speech engine).
/// </summary>
public interface IAudioProcessor
{
    /// <summary>The format produced by this processor.</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>
    /// Processes one chunk. When the input already matches the output format the chunk is passed
    /// through unchanged and the caller must treat it as read-only.
    /// </summary>
    /// <param name="input">The input chunk.</param>
    /// <param name="output">The processed chunk, or null when the processor could not produce output for this input.</param>
    /// <returns>True when an output chunk was produced.</returns>
    bool TryProcess(AudioChunk input, out AudioChunk? output);
}
