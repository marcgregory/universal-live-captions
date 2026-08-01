using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Core.Processing;

/// <summary>
/// Detects the presence of speech in a stream of audio chunks.
/// </summary>
public interface IVoiceActivityDetector
{
    /// <summary>
    /// Evaluates one chunk. The detector keeps state across calls so it can apply hysteresis
    /// (for example, ignoring very short bursts and holding speech through brief silences).
    /// </summary>
    /// <param name="chunk">The chunk to evaluate.</param>
    /// <returns>True when the detector considers the stream to contain speech.</returns>
    bool IsSpeech(AudioChunk chunk);

    /// <summary>Resets all internal state.</summary>
    void Reset();
}
