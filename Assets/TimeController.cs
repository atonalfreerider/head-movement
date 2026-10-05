using UnityEngine;
using VRTKLite.Controllers;

[RequireComponent(typeof(ControllerEvents))]
public class TimeController : MonoBehaviour
{
    void Awake()
    {
        ControllerEvents controllerEvents = GetComponent<ControllerEvents>();
        controllerEvents.ButtonOnePressed += HeadMovement.Instance.TogglePlayPause;
        controllerEvents.ButtonTwoPressed += HeadMovement.Instance.ToggleLoopMeasure;
        controllerEvents.RightButtonPressed += HeadMovement.Instance.SpeedUp;
        controllerEvents.LeftButtonPressed += HeadMovement.Instance.SlowDown;
        controllerEvents.UpButtonPressed += () => HeadMovement.Instance.StepMeasure(1);
        controllerEvents.DownButtonPressed += () => HeadMovement.Instance.StepMeasure(-1);
    }
}
