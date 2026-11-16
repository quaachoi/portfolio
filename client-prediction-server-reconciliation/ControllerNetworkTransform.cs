using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

[System.Serializable]
public struct ControllerTransformData : INetworkSerializable
{
    public int timeStamp;
    public Vector3 position;
    public Vector3 velocity;
    public Quaternion rotation;
    public int state;

    public static ControllerTransformData Empty => new ControllerTransformData
    {
        timeStamp = 0, position = Vector3.zero, rotation = Quaternion.identity, state = 0
    };

    public ControllerTransformData(Transform instance, Vector3 _velocity, int instanceState = 0)
    {
        timeStamp = NetworkManager.Singleton.NetworkTickSystem.ServerTime.Tick;
        position = instance.position;
        velocity = _velocity;
        rotation = instance.localRotation;
        state = instanceState;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref timeStamp);
        serializer.SerializeValue(ref position);
        serializer.SerializeValue(ref velocity);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref state);
    }
}

[System.Serializable]
public class ControllerTransformCache
{
    [SerializeField] CircularBuffer<ControllerTransformData> circularBuffer = new CircularBuffer<ControllerTransformData>();

    public void addTransformData(ControllerTransformData data)
    {
        if(circularBuffer.GetCount() != 0 && circularBuffer.GetHead().timeStamp >= data.timeStamp)
        {
            return;
        }
        circularBuffer.Add(data);
    }

    public void clear()
    {
        circularBuffer.Clear();
    }

    public bool tryGetTransformPair(double renderTime, out ControllerTransformData start, out ControllerTransformData finish)
    {
        start = ControllerTransformData.Empty;
        finish = ControllerTransformData.Empty;

        int count = circularBuffer.GetCount();
        if(count < 2)
        {
            return false;
        }

         // newest entry is older than renderTime, no data to interpolate toward
        if (circularBuffer.GetHeadOffset(0).timeStamp < renderTime)
        {
            return false;
        }

        for (int i = 0; i < count - 1; i++)
        {
            var newer = circularBuffer.GetHeadOffset(i);
            var older = circularBuffer.GetHeadOffset(i + 1);

            if (older.timeStamp <= renderTime)
            {
                start = older;    // earlier in time
                finish = newer;   // later in time
                return true;
            }
        }
        return false; // renderTime is older than everything buffered
    }

    public bool tryGetExact(int tick, out ControllerTransformData result)
    {
        result = ControllerTransformData.Empty;

        for (int i = 0; i < circularBuffer.GetCount(); i++)
        {
            ControllerTransformData element = circularBuffer.GetHeadOffset(i);
            if (element.timeStamp == tick) 
            { 
                result = element; 
                return true; 
            }
            if (element.timeStamp < tick) 
            {
                break;
            }
        }
        return false;
    }

    public ControllerTransformData getHead()
    {
        return circularBuffer.GetHead();
    }
}

public class ControllerNetworkTransform : NetworkBehaviour
{
    [SerializeField] protected int interpolationTickOffset = 2;
    public ControllerTransformCache transformCache;
    [Tooltip("Set if clients need to access velocity for things like prediction. Can be left null.")]
    [SerializeField] protected movementMotor motor;
    protected ragdollManager ragdollSystem;
    [SerializeField] protected Transform visualTransform;
    protected Vector3 visualTransformOffset;


    public void sendTransformData()
    {
        if(IsServer)
        {
            Vector3 velocity = Vector3.zero;
            if(motor != null)
            {
                velocity = motor.getVelocity();
            }
            ControllerTransformData data = new ControllerTransformData(transform, velocity);
            sendTransformDataClientRpc(data);
        }
    }
    [ClientRpc(Delivery = RpcDelivery.Unreliable)]
    protected void sendTransformDataClientRpc(ControllerTransformData instance)
    {
        receiveTransformData(instance);
    }
    protected virtual void receiveTransformData(ControllerTransformData instance)
    {
        transformCache.addTransformData(instance);
        if(!IsServer)
        {
            transform.position = transformCache.getHead().position;
        }
    }

    // Start is called before the first frame update
    public virtual void Start()
    {
        visualTransformOffset = visualTransform.position - transform.position;

        transformCache = new ControllerTransformCache();
        ragdollSystem = GetComponentInChildren<ragdollManager>(true);
    }

    protected virtual void interpolateVisuals()
    {
        if(ragdollSystem != null && ragdollSystem.ragdolled.Value)
        {
            return;
        }

        ControllerTransformData dataLastTick = ControllerTransformData.Empty;
        ControllerTransformData dataCurrentTick = ControllerTransformData.Empty;
        
        int offset = IsServer ? 0 : interpolationTickOffset;
        double renderTick = NetworkManager.Singleton.NetworkTickSystem.ServerTime.TickWithPartial - 1 - offset;

        if(!transformCache.tryGetTransformPair(renderTick, out dataLastTick, out dataCurrentTick))
        {
            Debug.Log("Failed to find transform pair for remote client");
            return;
        }

        if(visualTransform.position == dataCurrentTick.position)
        {
            return;
        }

        // float percentage = (float)(NetworkManager.Singleton.NetworkTickSystem.LocalTime.TickWithPartial - NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick);
        float percentage = (float)((renderTick - dataLastTick.timeStamp) / (dataCurrentTick.timeStamp - dataLastTick.timeStamp));

        visualTransform.position = Vector3.Lerp(dataLastTick.position + visualTransformOffset, dataCurrentTick.position + visualTransformOffset, percentage);
    }

    // Update is called once per frame
    public virtual void Update()
    {
        interpolateVisuals();
    }
}
