﻿using System;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.IO.Stores;

namespace osu.Framework.iOS.Audio
{
    public class iOSAudioManager : AudioManager
    {
        public iOSAudioManager(ResourceStore<byte[]> trackStore, ResourceStore<byte[]> sampleStore) : base(trackStore, sampleStore)
        {
        }

        protected override TrackManager CreateTrackManager(ResourceStore<byte[]> store) => new iOSTrackManager(store);

        protected override SampleManager CreateSampleManager(IResourceStore<byte[]> store) => new iOSSampleManager(store);
    }
}
