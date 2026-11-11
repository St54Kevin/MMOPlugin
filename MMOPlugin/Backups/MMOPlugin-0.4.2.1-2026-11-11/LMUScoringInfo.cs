using System.Runtime.InteropServices;

namespace MMOPlugin
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct LMUScoringInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] mTrackName;

        public int mSession;
        public double mCurrentET;
        public double mEndET;
        public int mMaxLaps;
        public double mLapDist;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] mResultsStreamPointer;

        public int mNumVehicles;
        public byte mGamePhase;
        public sbyte mYellowFlagState;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] mSectorFlag;

        public byte mStartLight;
        public byte mNumRedLights;

        [MarshalAs(UnmanagedType.I1)]
        public bool mInRealtime;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] mPlayerName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] mPlrFileName;

        public double mDarkCloud;
        public double mRaining;
        public double mAmbientTemp;
        public double mTrackTemp;

        public LMUVect3 mWind;

        public double mMinPathWetness;
        public double mMaxPathWetness;

        public byte mGameMode;

        [MarshalAs(UnmanagedType.I1)]
        public bool mIsPasswordProtected;

        public ushort mServerPort;
        public uint mServerPublicIP;
        public int mMaxPlayers;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] mServerName;

        public float mStartET;
        public double mAvgPathWetness;
        public float mSessionTimeRemaining;
        public float mTimeOfDay;

        [MarshalAs(UnmanagedType.I1)]
        public bool mIsFixedSetup;

        public byte mTrackGripLevel;
        public byte mCloudCoverage;
        public byte mTrackLimitsStepsPerPenalty;
        public byte mTrackLimitsStepsPerPoint;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 187)]
        public byte[] mExpansion;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] mVehiclePointer;
    }
}