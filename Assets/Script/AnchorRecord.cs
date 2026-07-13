using System;
using System.Collections.Generic;

[Serializable]
public class AnchorRecord
{
    public string sessionId;
    public string uuid;
    public string type;    // "marker" | "object"
    public string label;   // marker_0, object_0 ...
    public float px, py, pz;         // position
    public float rx, ry, rz, rw;     // rotation (quaternion)
    public string timestamp;
}

[Serializable]
public class AnchorRecordList
{
    public List<AnchorRecord> records = new List<AnchorRecord>();
}

[Serializable]
public class TrackPoint
{
    public float t;              // 시작 후 경과시간(초)
    public float px, py, pz;     // head position
    public float yaw;            // 수평 회전각(도)
}

[Serializable]
public class TrackLog
{
    public string sessionId;
    public float interval;
    public List<TrackPoint> points = new List<TrackPoint>();
}