#include "SenderAPI.h"

#include <atomic>

#include "FrameBuffer.h"
#include "Misc.h"


namespace {

struct Camera
{
    int                     m_slot;
    softcam::FrameBuffer    m_frame_buffer;
    softcam::Timer          m_timer;
};

// One sender per slot per process.
std::atomic<Camera*>    s_cameras[softcam::MAX_SLOTS];

bool ownsSlot(Camera* target)
{
    return target &&
           0 <= target->m_slot && target->m_slot < softcam::MAX_SLOTS &&
           s_cameras[target->m_slot].load() == target;
}

} //namespace


namespace softcam {
namespace sender {

CameraHandle    CreateCamera(int slot, int width, int height, float framerate)
{
    if (slot < 0 || slot >= MAX_SLOTS)
    {
        return nullptr;
    }
    if (auto fb = FrameBuffer::create(slot, width, height, framerate))
    {
        Camera* camera = new Camera{ slot, fb, Timer() };
        Camera* expected = nullptr;
        if (s_cameras[slot].compare_exchange_strong(expected, camera))
        {
            return camera;
        }
        delete camera;
    }
    return nullptr;
}

void            DeleteCamera(CameraHandle camera)
{
    Camera* target = static_cast<Camera*>(camera);
    if (target && ownsSlot(target) &&
        s_cameras[target->m_slot].compare_exchange_strong(target, nullptr))
    {
        target->m_frame_buffer.deactivate();
        delete target;
    }
}

void            SendFrame(CameraHandle camera, const void* image_bits)
{
    Camera* target = static_cast<Camera*>(camera);
    if (ownsSlot(target) && image_bits)
    {
        auto framerate = target->m_frame_buffer.framerate();
        auto frame_counter = target->m_frame_buffer.frameCounter();

        // To deliver frames in the regular period, we sleep here a bit
        // before we deliver the new frame if it's not the time yet.
        // If it's already the time, we deliver it immediately and
        // let the timer keep running so that if the next frame comes
        // in time the constant delivery recovers.
        // However if the delay grew too much (greater than 50 percent
        // of the period), we reset the timer to avoid continuing
        // irregular delivery.
        if (0.0f < framerate)
        {
            if (0 == frame_counter) // the first frame
            {
                target->m_timer.reset();
            }
            else
            {
                auto ref_delta = 1.0f / framerate;
                auto time = target->m_timer.get();
                if (time < ref_delta)
                {
                    Timer::sleep(ref_delta - time);
                }
                if (time < ref_delta * 1.5f)
                {
                    target->m_timer.rewind(ref_delta);
                }
                else
                {
                    target->m_timer.reset();
                }
            }
        }

        target->m_frame_buffer.write(image_bits);
    }
}

bool            WaitForConnection(CameraHandle camera, float timeout)
{
    Camera* target = static_cast<Camera*>(camera);
    if (ownsSlot(target))
    {
        Timer timer;
        while (!target->m_frame_buffer.connected())
        {
            if (0.0f < timeout && timeout <= timer.get())
            {
                return false;
            }
            Timer::sleep(0.001f);
        }
        return true;
    }
    return false;
}

bool            IsConnected(CameraHandle camera)
{
    Camera* target = static_cast<Camera*>(camera);
    if (ownsSlot(target))
    {
        return target->m_frame_buffer.connected();
    }
    return false;
}

unsigned        ReceiverHeartbeat(CameraHandle camera)
{
    Camera* target = static_cast<Camera*>(camera);
    if (ownsSlot(target))
    {
        return target->m_frame_buffer.receiverHeartbeat();
    }
    return 0;
}

} //namespace sender
} //namespace softcam
