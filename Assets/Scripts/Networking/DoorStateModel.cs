using Normal.Realtime;

/// <summary>
/// Normcore datastore model for one TouchDoor: whether it is open.
/// The model is unowned, so whichever client's player touches the door writes the new state.
/// </summary>
[RealtimeModel]
public partial class DoorStateModel
{
    [RealtimeProperty(1, true, true)]
    private bool _isOpen;
}
