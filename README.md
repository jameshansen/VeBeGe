# VeBeGe, hide people in your video call background, without blurring it

<img width="640" height="360" alt="meeting-demo" src="https://github.com/user-attachments/assets/cca46da2-c1ee-41ba-a0a3-31f79e4519fa" />

**VeBeGe** is a free webcam filter for Windows that removes intrusion from the background image, primarily people walking or appearing in the background using face detection, but also moving objects using motion tracking.

After installing VeBeGe, simply pick the **(VeBeGe)** version of your webcam in the settings of your meeting application.

> The name is just the initials: **V**irtual **B**ack**g**round → VBG → *Ve·Be·Ge*.

## The problem it solves

VeBeGe solves the issue of individuals and other intrusions from appearing in a standard webcam video feed.

Users can use their webcam normally, wherever they are, for example an office, home office, or coffee shop, without fearing unexpected intrusion into the background of the video frame, and without needing to apply a blanket blur to the entire background.

## How to use it

1. **Install VeBeGe.** No admin prompt, no reboot, it sets itself up on first run.
2. **Pick the (VeBeGe) camera.** In the app's camera menu, choose the entry ending in **(VeBeGe)**, e.g. *Logitech StreamCam (VeBeGe)*. If it doesn't appear right away, restart the meeting app.
3. **Done.** Join your calls as normal. A loading wheel appears for a few seconds while VeGeBe builds the virtual background.

## How it works

<img width="1149" height="1022" alt="image" src="https://github.com/user-attachments/assets/6a0fe1b4-6e9f-43d5-927e-a7467e6d897c" />

A typical meeting video is a person sitting at a camera, with the background behind them, so it first separates the foreground and background, using the same algorithm as a typical "background blur"

The background layer is processed through several stages. The first step is applying motion detection and intrusion detection algorithms (including face detection, people detection and/or object detection) to identify areas of intrusion or rapid change.

Areas without intrusion or change are memorized into a "virtual background" plane, a safe background that can be used for in-painting. Areas with intrusion, where we have memorized a safe "virtual background" replacement for that area, are in-painted using the virtual background.

Areas with intrusion where there is no “virtual background” available, are blurred/interpolated from the surrounding pixels.

## Good to know

- **Runs entirely on your computer.** No cloud, no uploads.
- **Your camera light stays off** until an app actually starts a call, VeBeGe doesn't hold the camera open in the background.
- **Works with DirectShow apps:** Zoom, Chrome/Edge (Meet), OBS, Discord, and more, on Windows 10 and 11 (64-bit).
- It mirrors up to **8** of your cameras, each getting its own **(VeBeGe)** version.

## Download & setup

Get the signed build from the **[Microsoft Store](https://apps.microsoft.com/detail/9PBSNVW5R7T3)**, that's the easiest way to install and it keeps itself up to date.

The unsigned MSI is on the **[latest GitHub release](https://github.com/jameshansen/VeBeGe/releases/latest)**.

**[vebege.io](https://vebege.io/)** also has a full setup guide and a live demo.

## Who made it

VeBeGe is written and maintained by **James Hansen**, [jameshansen.ai](https://jameshansen.ai).

It grew out of an earlier tool called **[JustShowMe](https://github.com/jameshansen/JustShowMe)**, where this background-removal idea was first built, see that repo for more of the development history.

It's free; feedback and ideas are genuinely welcome through the **[contact form](https://vebege.io/contact)** on the site.

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

[ponytail](https://github.com/DietrichGebert/ponytail/), used with Claude Code
during development to keep the implementation lean.
