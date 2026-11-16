using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

[System.Serializable]
public struct LocalControllerTransformData
{
    public int timeStamp;
    public Vector3 position;
    public Quaternion rotation;
    public int state;

    public static LocalControllerTransformData Empty => new LocalControllerTransformData
    {
        timeStamp = 0, position = Vector3.zero, rotation = Quaternion.identity, state = 0
    };

    public LocalControllerTransformData(Transform instance, int instanceState = 0)
    {
        timeStamp = NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick;
        rotation = instance.localRotation;
        position = instance.position;
        state = instanceState;
    }
}

[System.Serializable]
public class LocalControllerTransformCache
{
    [SerializeField] CircularBuffer<LocalControllerTransformData> circularBuffer = new CircularBuffer<LocalControllerTransformData>();

    public void addTransformData(LocalControllerTransformData data)
    {
        if(circularBuffer.GetCount() != 0 && circularBuffer.GetHead().timeStamp > data.timeStamp)
        {
            return;
        }
        circularBuffer.Add(data);
    }

    public void clear()
    {
        circularBuffer.Clear();
    }

    public bool tryGetExact(int tick, out LocalControllerTransformData result)
    {
        result = LocalControllerTransformData.Empty;

        for (int i = 0; i < circularBuffer.GetCount(); i++)
        {
            LocalControllerTransformData element = circularBuffer.GetHeadOffset(i);
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

    public LocalControllerTransformData getHead()
    {
        return circularBuffer.GetHead();
    }

    public void getLastTickAndCurrentTickPosition(out Vector3 lastTickPos, out Vector3 currentTickPos)
    {
        currentTickPos = circularBuffer.GetHead().position;
        lastTickPos = circularBuffer.GetHeadOffset(1).position;
    }
}

public class PlayerControllerNetworkTransform : ControllerNetworkTransform
{
    static int reconciliationOffset = 2;

    [SerializeField] protected LocalControllerTransformCache localTransformCache;

    [Header("Player Components")]
    [SerializeField] PlayerInputManager inputManager;
    [SerializeField] movementManager movement;

    public void storeLocalTransformData()
    {
        LocalControllerTransformData data = new LocalControllerTransformData(transform, (int)movement.getCurrentState());
        localTransformCache.addTransformData(data);
    }
    
    protected override void receiveTransformData(ControllerTransformData instance)
    {
        transformCache.addTransformData(instance);
        if(!IsServer && !IsOwner)
        {
            transform.position = transformCache.getHead().position;
        }
    }

    public override void Start()
    {
        base.Start();
        localTransformCache = new LocalControllerTransformCache();
    }

    protected override void interpolateVisuals()
    {
        if(IsOwner)
        {
            if(ragdollSystem != null && ragdollSystem.ragdolled.Value)
            {
                return;
            }

            Vector3 positionCurrentTick;
            Vector3 positionLastTick;

            localTransformCache.getLastTickAndCurrentTickPosition(out positionLastTick, out positionCurrentTick);

            if(visualTransform.position == positionCurrentTick)
            {
                return;
            }

            float percentage = (float)(NetworkManager.Singleton.NetworkTickSystem.LocalTime.TickWithPartial - NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick);
            
            visualTransform.position = Vector3.Lerp(positionLastTick + visualTransformOffset, positionCurrentTick + visualTransformOffset, percentage);
        }
        else
        {
            base.interpolateVisuals();
        }
    }

    public override void Update()
    {
        interpolateVisuals();
    }

    int skipUntilServerTick = 0;

    public void clientReconciliation()
    {
        ControllerTransformData serverData = transformCache.getHead();   // newest snapshot we have
        
        if (serverData.timeStamp <= skipUntilServerTick) 
        {
            return;
        }

        LocalControllerTransformData localData = LocalControllerTransformData.Empty;

        if (!localTransformCache.tryGetExact(serverData.timeStamp, out localData))
        {
            return;
        }

        if ((localData.position - serverData.position).sqrMagnitude >= 0.1f * 0.1f)
        {
            Debug.DrawRay(localData.position, Vector3.up*1.5f, Color.green, 3f);
            Debug.DrawRay(serverData.position, Vector3.up*1.5f, Color.blue, 3f);
            int ticks = NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick - serverData.timeStamp;
            reconcile(serverData.position, serverData.velocity, ticks);
        }
    }

    void reconcile(Vector3 correctPosition, Vector3 velocity, int ticks)
    {
        // Debug.Log($"Replaying {ticks} ticks");
        movement.teleport(correctPosition);
        movement.setVelocity(velocity);
        // Debug.DrawRay(correctPosition, Vector3.up, Color.green, 3f);
        for(int i = ticks - 1; i >= 0; i--)
        {
            inputManager.setEvaluationOffset(i);
            movement.evaluateMovementStatesReplay();

            // Debug.Log($"replay step {i}");
            Debug.DrawRay(transform.position, Vector3.up, Color.magenta, 3f);
        }

        inputManager.removeEvaluationOffset();
        skipUntilServerTick = NetworkManager.Singleton.NetworkTickSystem.LocalTime.Tick;
    }
}
