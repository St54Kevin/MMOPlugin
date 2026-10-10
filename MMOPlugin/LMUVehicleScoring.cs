using System.Runtime.InteropServices;

namespace MMOPlugin
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct LMUVect3
    {
        public double x;
        public double y;
        public double z;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct LMUVehicleScoring
    {
        public int mID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] mDriverName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] mVehicleName;

        public short mTotalLaps;
        public sbyte mSector;
        public sbyte mFinishStatus;

        public double mLapDist;
        public double mPathLateral;
        public double mTrackEdge;

        public double mBestSector1;
        public double mBestSector2;
        public double mBestLapTime;

        public double mLastSector1;
        public double mLastSector2;
        public double mLastLapTime;

        public double mCurSector1;
        public double mCurSector2;

        public short mNumPitstops;
        public short mNumPenalties;

        [MarshalAs(UnmanagedType.I1)]
        public bool mIsPlayer;

        public sbyte mControl;

        [MarshalAs(UnmanagedType.I1)]
        public bool mInPits;

        public byte mPlace;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] mVehicleClass;

        public double mTimeBehindNext;
        public int mLapsBehindNext;

        public double mTimeBehindLeader;
        public int mLapsBehindLeader;

        public double mLapStartET;

        public LMUVect3 mPos;
        public LMUVect3 mLocalVel;
        public LMUVect3 mLocalAccel;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public LMUVect3[] mOri;

        public LMUVect3 mLocalRot;
        public LMUVect3 mLocalRotAccel;

        public byte mHeadlights;
        public byte mPitState;
        public byte mServerScored;
        public byte mIndividualPhase;

        public int mQualification;

        public double mTimeIntoLap;
        public double mEstimatedLapTime;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] mPitGroup;

        public byte mFlag;

        [MarshalAs(UnmanagedType.I1)]
        public bool mUnderYellow;

        public byte mCountLapFlag;

        [MarshalAs(UnmanagedType.I1)]
        public bool mInGarageStall;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] mUpgradePack;

        public float mPitLapDist;
        public float mBestLapSector1;
        public float mBestLapSector2;

        public ulong mSteamID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] mVehFilename;

        public short mAttackMode;
        public byte mFuelFraction;

        [MarshalAs(UnmanagedType.I1)]
        public bool mDRSState;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] mExpansion;
    }
}