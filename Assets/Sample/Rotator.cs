// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using UnityEngine;

public class Rotator : MonoBehaviour
{
    #region Serialized Fields

    [SerializeField] private float speed;

    #endregion

    #region Event Functions

    private void Update()
    {
        transform.localRotation *= Quaternion.Euler(0, speed * Time.deltaTime, 0);
    }

    #endregion
}
