using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

public class MicrophoneHandler : MonoBehaviour
{
    public bool IsRecording { get; private set; }

    private AudioClip recording;
    private int maxRecordingTime = 300;

    // This is set through a dropdown in the editor. Impl. in TextToSpeechRequestEditor.cs
    [Header("Microphone input")]
    [HideInInspector] public string selectedMicString;

    private string micStringThatIsRecording;

    [Header("Feedback sounds")]
    private AudioSource feedbackAudioSource;

    [SerializeField] private AudioSource vrAudioSrc;
    [SerializeField] private AudioSource pcAudioSrc;
    [SerializeField] private AudioClip micUnavailableSound, micOnSound, micOffSound;
    [SerializeField] private AudioClip newTaskAvailableSound;

    private int startSample = 0;
    private int endSample = 0;

    private IEnumerator Start()
    {
        if (vrAudioSrc != null && vrAudioSrc.isActiveAndEnabled)
            feedbackAudioSource = vrAudioSrc;
        else if (pcAudioSrc != null && pcAudioSrc.isActiveAndEnabled)
            feedbackAudioSource = pcAudioSrc;
        else
            Debug.LogError("No feedback audio source available.");

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
            // Poll until the user responds (up to 30 s)
            float elapsed = 0f;
            while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && elapsed < 30f)
            {
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
            }
        }

        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Debug.LogError("Microphone permission denied.");
            yield break;
        }

        yield return null; // one frame for permission to propagate
#else
        yield return null;
#endif

        if (Microphone.devices.Length > 0)
            Debug.Log($"Microphone devices available. Selected: {selectedMicString}");
        else
            Debug.LogError("No microphone devices available.");

        yield return new WaitForSeconds(0.5f);
        StartMicInput();
    }

    public void StartMicInputAfterMicChange()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Application is not playing. Microphone input will not start.");
        }

        StopRecording();
        Microphone.End(micStringThatIsRecording);

        StartMicInput();
    }

    // Returns selectedMicString if it exists on this platform, otherwise null (default device).
    private string ResolveDeviceName()
    {
        if (string.IsNullOrEmpty(selectedMicString))
            return null;

        foreach (string device in Microphone.devices)
        {
            if (device == selectedMicString)
                return selectedMicString;
        }

        Debug.LogWarning($"Microphone '{selectedMicString}' not found on this platform. Falling back to default device.");
        return null;
    }

    private void StartMicInput()
    {
        string deviceToUse = ResolveDeviceName();
        recording = Microphone.Start(deviceToUse, true, maxRecordingTime, 16000);
        micStringThatIsRecording = deviceToUse;

        // Always reset the indicator regardless of success, so it can't get stuck on.
        ToggleMicIsRecordingUI(false);

        if (recording == null)
        {
            Debug.LogError("Failed to start microphone.");
            return;
        }

        Debug.Log($"Microphone started (device: '{deviceToUse ?? "default"}') and is recording continuously.");
    }

    public void StartRecording()
    {
        if (recording == null)
        {
            Debug.LogError("No microphone available.");
            return;
        }

        IsRecording = true;
        startSample = Microphone.GetPosition(micStringThatIsRecording);
        PlayMicSound(true);
        ToggleMicIsRecordingUI(true);
        Debug.Log("Recording start marker set at sample: " + startSample);
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        endSample = Microphone.GetPosition(micStringThatIsRecording);
        IsRecording = false;
        PlayMicSound(false);
        ToggleMicIsRecordingUI(false);
        Debug.Log("Recording end marker set at sample: " + endSample);
    }

    private static void ToggleMicIsRecordingUI(bool value)
    {
        StudyTasks.SetMicActiveObjects(value);
    }

    public byte[] GetLatestMicAudioBytes()
    {
        if (recording == null)
        {
            Debug.LogError("No recording available.");
            return null;
        }

        int sampleCount = endSample - startSample;
        if (sampleCount < 0) sampleCount += recording.samples;

        AudioClip finalClip = CutClipAtSampleRange(recording, startSample, sampleCount);
        var handler = new AudioMemoryStreamHandler();
        MemoryStream audioStream = handler.PrepareAudioStream(finalClip);

        return audioStream.ToArray();
    }

    private AudioClip CutClipAtSampleRange(AudioClip recordedClip, int startSample, int sampleCount)
    {
        float[] samples = new float[sampleCount * recordedClip.channels];
        recordedClip.GetData(samples, startSample);
        AudioClip cutClip = AudioClip.Create("CutClip", sampleCount, recordedClip.channels, recordedClip.frequency, false);
        cutClip.SetData(samples, 0);
        return cutClip;
    }

    public static byte[] GetAudioBytesFromClip(AudioClip inputClip)
    {
        var handler = new AudioMemoryStreamHandler();
        MemoryStream audioStream = handler.PrepareAudioStream(inputClip);
        return audioStream.ToArray();
    }

    private void PlayMicSound(bool value)
    {
        feedbackAudioSource.clip = value ? micOnSound : micOffSound;
        feedbackAudioSource.PlayOneShot(feedbackAudioSource.clip);
    }

    public void PlayMicUnavailableSound()
    {
        feedbackAudioSource.clip = micUnavailableSound;
        feedbackAudioSource.PlayOneShot(feedbackAudioSource.clip);
    }

    public void PlayNewTaskAvailableNotificationSound()
    {
        feedbackAudioSource.clip = newTaskAvailableSound;
        feedbackAudioSource.PlayOneShot(feedbackAudioSource.clip);
    }
}
