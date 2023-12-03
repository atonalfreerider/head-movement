using UnityEngine;
using VRTKLite.Controllers;

[RequireComponent(typeof(ControllerEvents))]
public class TimeController : MonoBehaviour
{
    void Awake()
    {
        ControllerEvents controllerEvents = GetComponent<ControllerEvents>();
        controllerEvents.ButtonOnePressed += HeadMovement.Instance.TogglePlayPause;
        controllerEvents.RightButtonPressed += HeadMovement.Instance.SpeedUp;
        controllerEvents.LeftButtonPressed += HeadMovement.Instance.SlowDown;
    }
}