# VeBeGe, hide people in your video call background, without blurring it

**VeBeGe** is a free virtual webcam for Windows that quietly removes anyone who
isn't *you* from your video call, family, roommates, a coworker walking past,
**without blurring your room**. Your background stays sharp and real; the people
you didn't invite into shot simply never appear.

No editing. No buttons to press mid-call. Pick the **(VeBeGe)** version of your
camera once, and it just works from then on.

> The name is just the initials: **V**irtual **B**ack**g**round → VBG → *Ve·Be·Ge*.

## The problem it solves

Every other tool gives you a **blur**, which hides your *whole* room and still
shows a moving blob when someone walks by. That's the wrong fix if all you
wanted was to stop your kid, your roommate, or a stranger from photobombing your
call.

VeBeGe does something different: it learns what your empty room looks like, and
when a person appears where they shouldn't be, it paints them out using the real
background, so your room stays visible and everyone else is gone.

## How to use it

1. **Install VeBeGe.** No admin prompt, no reboot, it sets itself up on first
   run.
2. **Restart your meeting software.** If Zoom, Google Meet, Microsoft Teams,
   OBS or Discord was already open, quit it completely and open it again. Most
   apps only read the camera list when they start, so this is what makes the
   **(VeBeGe)** entries show up.
3. **Pick the (VeBeGe) camera.** In the app's camera menu, choose the entry
   ending in **(VeBeGe)**, e.g. *Logitech StreamCam (VeBeGe)*.
4. **That's it.** Join your calls as normal. Anyone who isn't you is removed
   automatically, every time, and your choice is remembered.

## How it works (the short version)

VeBeGe builds a few layers out of your live camera feed, many times per second:

1. **What your camera sees**, the real feed, you plus whoever wanders in.
2. **Motion, spotted**, it flags whatever is moving that isn't you, frame by
   frame.
3. **Faces, detected**, face detection combines with the motion to pick out the
   people behind you, so they can be removed from the background.
4. **You, isolated**, it cuts your outline out of the scene so you can be placed
   back on top.
5. **Your room, learned**, it quietly rebuilds the empty background behind
   people as parts of it become visible.
6. **What the call gets**, you, composited back onto your real, people-free
   room. Everyone else is gone before your video app ever sees them.

You can see all six layers side by side on the [website](https://vebege.io/#how).

## Good to know

- **Runs entirely on your computer.** No cloud, no uploads, ever.
- **Your camera light stays off** until an app actually starts a call, VeBeGe
  doesn't hold the camera open in the background.
- **Works with DirectShow apps:** Zoom, Chrome/Edge (Meet), OBS, Discord, and
  more, on Windows 10 and 11 (64-bit).
- It mirrors up to **8** of your cameras, each getting its own **(VeBeGe)**
  version.

## A couple of limits

- The Windows Camera app and the newest Microsoft Teams desktop client use a
  different camera system and won't see virtual cameras like VeBeGe.
- Someone who stands *completely* still behind you for a long time can
  eventually blend into the learned background, VeBeGe is built for people
  moving through shot, which is the common case.

## Download & setup

Get the signed build from the
**[Microsoft Store](https://apps.microsoft.com/detail/9PBSNVW5R7T3)**, that's the
easiest way to install and it keeps itself up to date.

Prefer to install it yourself? The unsigned MSI is on the
**[latest GitHub release](https://github.com/jameshansen/VeBeGe/releases/latest)**.

**[vebege.io](https://vebege.io/)** also has a full setup guide and a live demo.

## Who made it

VeBeGe is made by **James Hansen**, [jameshansen.ai](https://jameshansen.ai) ·
[GitHub](https://github.com/jameshansen). It grew out of an earlier tool called
**[JustShowMe](https://github.com/jameshansen/JustShowMe)**, where this
background-removal idea was first built, see that repo for more of the
development history. It's free; feedback and ideas are genuinely welcome through
the **[contact form](https://vebege.io/contact)** on the site.

## Special Thanks

The virtual camera is built on [softcam](https://github.com/tshino/softcam) by
tshino (MIT licensed), the DirectShow virtual-camera base VeBeGe's driver is
forked from.

The [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet)
face-detection model (Shiqi Yu et al.), distributed via the OpenCV Zoo, used to
spot people in the frame.

The [PP-HumanSeg](https://github.com/opencv/opencv_zoo/tree/main/models/human_segmentation_pphumanseg)
human-segmentation model (PaddlePaddle / Baidu), distributed via the OpenCV Zoo,
used for the foreground/background mask.
