using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VeBeGe
{
    /// Lightweight IoU tracker. Keeps a face "alive" for MaxAge frames after
    /// the detector stops seeing it, the "staytime", so a head turning
    /// through an angle the detector momentarily misses keeps being excluded
    /// from the learned background instead of baking in.
    /// ponytail: greedy IoU match, no Kalman/Hungarian, fine at these counts.
    /// (JustShowMe's appearance-embedding fallback is gone with recognition.)
    internal sealed class FaceTracker
    {
        public sealed class Track
        {
            public Rect Box;
            public int Age;   // frames since last detection (0 = seen this frame)
            public int Quiet; // frames since motion corroborated this track (caller-maintained)
        }

        private readonly List<Track> _tracks = new List<Track>();
        private readonly double _iouThreshold;

        /// Keep a track alive this many frames after its last detection.
        public int MaxAge { get; set; }

        // iou 0.2 tolerates the box growing/shrinking with pose.
        public FaceTracker(double iouThreshold = 0.2, int maxAge = 90)
        {
            _iouThreshold = iouThreshold;
            MaxAge = maxAge;
        }

        /// Feeds this frame's detections in; returns every active track,
        /// including recently-lost ones within the staytime window. Tracks are
        /// live objects: the caller may maintain Quiet across frames.
        public IReadOnlyList<Track> Update(IReadOnlyList<Rect> detections)
        {
            foreach (var t in _tracks) t.Age++;

            var trackUsed = new bool[_tracks.Count];
            var unmatched = new List<Rect>();

            for (int d = 0; d < detections.Count; d++)
            {
                int best = -1;
                double bestIou = _iouThreshold;
                for (int i = 0; i < _tracks.Count; i++)
                {
                    if (trackUsed[i]) continue;
                    double iou = IoU(_tracks[i].Box, detections[d]);
                    if (iou > bestIou) { bestIou = iou; best = i; }
                }
                if (best >= 0)
                {
                    _tracks[best].Box = detections[d];
                    _tracks[best].Age = 0;
                    trackUsed[best] = true;
                }
                else
                {
                    unmatched.Add(detections[d]);
                }
            }

            foreach (var box in unmatched)
                _tracks.Add(new Track { Box = box, Age = 0 });

            _tracks.RemoveAll(t => t.Age > MaxAge);
            return _tracks;
        }

        private static double IoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.Left, b.Left), y1 = Math.Max(a.Top, b.Top);
            int x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
            int iw = x2 - x1, ih = y2 - y1;
            if (iw <= 0 || ih <= 0) return 0;
            double inter = (double)iw * ih;
            double union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
            return union <= 0 ? 0 : inter / union;
        }
    }
}
