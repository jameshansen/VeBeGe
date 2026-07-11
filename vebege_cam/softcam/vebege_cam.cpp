#include "vebege_cam.h"

#include <olectl.h>
#include <initguid.h>

#include <softcam_core/DShowSoftcam.h>
#include <softcam_core/SenderAPI.h>


// VeBeGe registers one DirectShow source filter per mirrored physical webcam.
// Each slot gets its own CLSID from this family (only the last byte varies)
// and its own shared-memory channel. The VeBeGe service decides which slots
// are registered and what their friendly names are (registry Instance keys);
// this DLL just serves whatever slot it's instantiated as.
//
// {E1B45C30-8A2D-4F6B-9C47-5A3E1D2B70xx}, xx = slot 00..07.
// Must stay in sync with DriverRegistrar.cs in vebege_service.
#define VBG_DEFINE_SLOT_GUID(n) \
    DEFINE_GUID(CLSID_VBGCam##n, \
    0xe1b45c30, 0x8a2d, 0x4f6b, 0x9c, 0x47, 0x5a, 0x3e, 0x1d, 0x2b, 0x70, 0x0##n);

VBG_DEFINE_SLOT_GUID(0)
VBG_DEFINE_SLOT_GUID(1)
VBG_DEFINE_SLOT_GUID(2)
VBG_DEFINE_SLOT_GUID(3)
VBG_DEFINE_SLOT_GUID(4)
VBG_DEFINE_SLOT_GUID(5)
VBG_DEFINE_SLOT_GUID(6)
VBG_DEFINE_SLOT_GUID(7)


namespace {

const GUID* const kSlotClsids[softcam::MAX_SLOTS] =
{
    &CLSID_VBGCam0, &CLSID_VBGCam1, &CLSID_VBGCam2, &CLSID_VBGCam3,
    &CLSID_VBGCam4, &CLSID_VBGCam5, &CLSID_VBGCam6, &CLSID_VBGCam7,
};

template <int SLOT>
CUnknown * WINAPI CreateSoftcamInstance(LPUNKNOWN lpunk, HRESULT *phr)
{
    return softcam::Softcam::CreateInstance(lpunk, *kSlotClsids[SLOT], SLOT, phr);
}

} // namespace

// COM global table of objects in this dll. The display names apps see come
// from the per-slot FriendlyName the service writes at registration time
// ("<physical camera name> (VeBeGe)"); these are just the class names.

CFactoryTemplate g_Templates[] =
{
    { L"VeBeGe Virtual Camera 1", &CLSID_VBGCam0, &CreateSoftcamInstance<0>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 2", &CLSID_VBGCam1, &CreateSoftcamInstance<1>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 3", &CLSID_VBGCam2, &CreateSoftcamInstance<2>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 4", &CLSID_VBGCam3, &CreateSoftcamInstance<3>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 5", &CLSID_VBGCam4, &CreateSoftcamInstance<4>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 6", &CLSID_VBGCam5, &CreateSoftcamInstance<5>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 7", &CLSID_VBGCam6, &CreateSoftcamInstance<6>, NULL, nullptr },
    { L"VeBeGe Virtual Camera 8", &CLSID_VBGCam7, &CreateSoftcamInstance<7>, NULL, nullptr },
};
int g_cTemplates = sizeof(g_Templates) / sizeof(g_Templates[0]);


// Developer convenience only (regsvr32, needs admin): registers the COM
// classes under HKCR. Normal installs never use this, the VeBeGe service
// writes per-user HKCU\Software\Classes entries (COM class + DirectShow
// category instance per active slot) itself, no elevation needed.

STDAPI DllRegisterServer()
{
    return AMovieDllRegisterServer2(TRUE);
}

STDAPI DllUnregisterServer()
{
    return AMovieDllRegisterServer2(FALSE);
}

extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE, ULONG, LPVOID);

BOOL APIENTRY DllMain(HANDLE hModule, DWORD  dwReason, LPVOID lpReserved)
{
    return DllEntryPoint((HINSTANCE)(hModule), dwReason, lpReserved);
}


//
// VeBeGe Sender API (used by the VeBeGe service)
//

extern "C" scCamera scCreateCamera(int slot, int width, int height, float framerate)
{
    return softcam::sender::CreateCamera(slot, width, height, framerate);
}

extern "C" void     scDeleteCamera(scCamera camera)
{
    return softcam::sender::DeleteCamera(camera);
}

extern "C" void     scSendFrame(scCamera camera, const void* image_bits)
{
    return softcam::sender::SendFrame(camera, image_bits);
}

extern "C" bool     scWaitForConnection(scCamera camera, float timeout)
{
    return softcam::sender::WaitForConnection(camera, timeout);
}

extern "C" bool     scIsConnected(scCamera camera)
{
    return softcam::sender::IsConnected(camera);
}

extern "C" unsigned scReceiverHeartbeat(scCamera camera)
{
    return softcam::sender::ReceiverHeartbeat(camera);
}
