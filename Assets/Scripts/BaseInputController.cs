using System;
using UnityEngine;

public abstract class BaseInputController : MonoBehaviour
{
    [SerializeField]
    private Transform _container;
    protected Transform container => _container;
}
