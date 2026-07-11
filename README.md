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
2. **Open your video app** (Zoom, Google Meet, Microsoft Teams in a browser,
   OBS, Discord…) and in the camera menu pick the entry ending in **(VeBeGe)**,
   e.g. *Logitech StreamCam (VeBeGe)*.
3. **That's it.** Join your calls as normal. Anyone who isn't you is removed
   automatically, every time.

## How it works (the short version)

VeBeGe builds a few layers out of your live camera feed, many times per second:

1. **What your camera sees**, the real feed, you plus whoever wanders in.
2. **Motion, spotted**, it flags whatever is moving that isn't you.
3. **Your room, learned**, it quietly rebuilds the empty background behind
   people as parts of it become visible.
4. **What the call gets**, you, composited back onto your real, people-free
   room. Everyone else is gone before your video app ever sees them.

You can see all four layers side by side on the [website](https://vebege.io/#how).

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

Grab it from **[vebege.io](https://vebege.io/)**, which also has a full setup
guide and a live demo.

## Who made it

VeBeGe is made by **James Hansen**, [jameshansen.ai](https://jameshansen.ai) ·
[GitHub](https://github.com/jameshansen). It grew out of an earlier tool called
**[JustShowMe](https://github.com/jameshansen/JustShowMe)**, where this
background-removal idea was first built, see that repo for more of the
development history. It's free; feedback and ideas are genuinely welcome through
the contact form on the site.
