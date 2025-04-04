// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.IO;
using InstantReplay;
using UnityEngine;
using UnityEngine.UI;

public class Test : MonoBehaviour
{
    #region Serialized Fields

    [SerializeField] private Button _button;
    [SerializeField] private Image _progressImage;

    #endregion

    private InstantReplaySession _currentSession;

    private float _progress;

    #region Event Functions

    private void Start()
    {
        _button.onClick.AddListener(() =>
        {
            if (_currentSession is { State: SessionState.Recording })
            {
                StopAndTranscode();
            }
            else
            {
                _currentSession?.Dispose();
                _currentSession = new InstantReplaySession(900, maxWidth: 640,
                    maxHeight: 640);
            }
        });
    }

    private void Update()
    {
        if (_currentSession is { State: SessionState.Recording })
            _button.GetComponentInChildren<Text>().text = $"Stop: {_currentSession.NumBusySlots}";
        else
            _button.GetComponentInChildren<Text>().text = "Start";

        if (_currentSession is { State: SessionState.Transcoding })
            _progressImage.fillAmount = _progress;
        else
            _progressImage.fillAmount = 0;
    }

    private void OnDestroy()
    {
        _currentSession?.Dispose();
    }

    #endregion

    private async void StopAndTranscode()
    {
        try
        {
            var output =
                await _currentSession.StopAndTranscodeAsync(new Progress<float>(progress => { _progress = progress; }),
                    destroyCancellationToken);

            var dest = Path.Combine(Application.persistentDataPath, Path.GetFileName(output));
            File.Move(output, dest);

            Debug.Log($"Completed: {dest}");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        finally
        {
            _progress = 0;
        }
    }
}
