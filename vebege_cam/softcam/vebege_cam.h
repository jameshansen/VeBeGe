#pragma once


//
// VeBeGe Sender API
//
// The VeBeGe service uses this C API (P/Invoke) to feed frames into the
// per-slot virtual cameras served by this DLL.
//

#define SOFTCAM_API __cdecl

extern "C"
{
    using scCamera = void*;

    /*
        Creates the virtual camera sender for the given slot (0..7).
        Width/height must be positive multiples of four. Returns null if the
        slot is invalid or a sender for that slot already exists.
    */
    scCamera    SOFTCAM_API scCreateCamera(int slot, int width, int height, float framerate = 60.0f);

    /*
        Deletes the sender; consuming apps fall back to the placeholder image.
    */
    void        SOFTCAM_API scDeleteCamera(scCamera camera);

    /*
        Sends one BGR24 bottom-up-agnostic frame (the driver flips). Paces to
        the camera framerate by sleeping as needed.
    */
    void        SOFTCAM_API scSendFrame(scCamera camera, const void* image_bits);

    /*
        Waits until an app has (ever) connected to this camera. timeout <= 0
        waits forever. Returns whether a connection happened.
    */
    bool        SOFTCAM_API scWaitForConnection(scCamera camera, float timeout = 0.0f);

    /*
        True once an app has ever connected to this camera (never resets,
        use scReceiverHeartbeat for liveness).
    */
    bool        SOFTCAM_API scIsConnected(scCamera camera);

    /*
        A counter the DirectShow filter bumps on every frame it serves while
        an app is actually streaming. If it stops changing, no one is
        watching and the physical camera can be released.
    */
    unsigned    SOFTCAM_API scReceiverHeartbeat(scCamera camera);
}
