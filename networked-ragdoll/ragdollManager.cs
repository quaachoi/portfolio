using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class ragdollManager : NetworkBehaviour
{
    [System.Serializable]
    protected struct RagdollPoseData : INetworkSerializable
    {
        public int timeStamp;
        public Vector3 rootBonePosition;
        public Quaternion[] boneRotations;

        public static RagdollPoseData Empty => new RagdollPoseData
        {
            timeStamp = 0, rootBonePosition = Vector3.zero, boneRotations = new Quaternion[0]
        };

        public RagdollPoseData(hurtBox[] hurtboxArray)
        {
            timeStamp = NetworkManager.Singleton.NetworkTickSystem.ServerTime.Tick;
            boneRotations = new Quaternion[hurtboxArray.Length];
            for(int i = 0; i < hurtboxArray.Length; i++)
            {
                if(hurtboxArray[i].dismembered)
                {
                    continue;
                }
                boneRotations[i] = hurtboxArray[i].transform.localRotation;
            }
            rootBonePosition = hurtboxArray[0].transform.position;
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            int length = serializer.IsWriter ? boneRotations.Length : 0;

            serializer.SerializeValue(ref timeStamp);
            serializer.SerializeValue(ref length);
            serializer.SerializeValue(ref rootBonePosition);

            if(serializer.IsReader)
            {
                boneRotations = new Quaternion[length];
            }

            for(int i = 0; i < length; i++)
            {
                serializer.SerializeValue(ref boneRotations[i]);
            }
        }
    }
    
    [System.Serializable]
    protected class RagdollPoseDataCache
    {
        [SerializeField] CircularBuffer<RagdollPoseData> circularBuffer = new CircularBuffer<RagdollPoseData>();

        public void addPoseData(RagdollPoseData data)
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

        public bool tryGetPosePair(double renderTime, out RagdollPoseData start, out RagdollPoseData finish)
        {
            start = RagdollPoseData.Empty;
            finish = RagdollPoseData.Empty;
            if(circularBuffer.GetCount() < 2)
            {
                return false;
            }

            //search array for timestamp
            for(int i = 0; i < circularBuffer.GetCount() - 1; i++)
            {
                int startTickTime = circularBuffer.GetDirectBufferElement(i).timeStamp;
                int endTickTime = circularBuffer.GetDirectBufferElement(i+1).timeStamp;

                if(startTickTime <= renderTime && endTickTime >= renderTime)
                {
                    start = circularBuffer.GetDirectBufferElement(i);
                    finish = circularBuffer.GetDirectBufferElement(i+1);
                    return true;
                }
            }
            return false;
        }
    }

    static int interpolationDelay = 3;

    [HideInInspector] public hurtBox[] hurtBoxes;
    [HideInInspector] public Rigidbody hipBoneRB;
    [HideInInspector] public bool ragdolledPreviousFrame = false;
    [HideInInspector] public Collider colliderComponent;

    public NetworkVariable<bool> ragdolled = new(writePerm: NetworkVariableWritePermission.Server);
    
    public healthMain health;
    public GameObject character;
    public GameObject hipBone;
    public audioManager audioPlayer;
    public AudioClip enterRagdollClip;
    public knockbackManager knockback;
    public BaseNetworkAnimator animator;
    public LayerMask groundMask;

    public float getUpTime = 0.5f;
    float elapsedGetUpTime = 0f;

    //Ragdoll Caching

    [SerializeField] RagdollPoseDataCache poseCache; 

    // public ikManager entityIKManager;

    /// <summary>
    /// Actions are run on both server and client
    /// </summary>
    public List<System.Action> actionsOnEnteringRagdoll = new List<System.Action>();
    /// <summary>
    /// Actions are run on both server and client
    /// </summary>
    public List<System.Action> actionsOnExitingRagdoll = new List<System.Action>();

    public void sendRagdollPoseData()
    {
        if(IsServer && !health.dead.Value && ragdolled.Value)
        {
            RagdollPoseData data = new RagdollPoseData(hurtBoxes);
            sendRagdollPoseDataClientRpc(data);
        }
    }
    [ClientRpc(Delivery = RpcDelivery.Unreliable)]
    protected virtual void sendRagdollPoseDataClientRpc(RagdollPoseData data)
    {
        poseCache.addPoseData(data);
    }

    public virtual void Start()
    {
        hipBoneRB = hipBone.GetComponent<Rigidbody>();
        colliderComponent = GetComponent<Collider>();
        hurtBoxes = character.GetComponentsInChildren<hurtBox>();
        foreach(var hurtbox in hurtBoxes)
        {
            if(hurtbox.transform != transform)
            {
                hurtbox.setRBKinematic();
            }
        }
        poseCache = new RagdollPoseDataCache();
    }

    public virtual void Update()
    {
        // Debug.DrawRay(hipBone.transform.position, hipBone.transform.forward, Color.green);
        if(IsServer)
        {
            exitRagdollConditions();
        }
        if(!IsServer && ragdolled.Value)
        {
            clientRagdollInterpolation();
        }
    }

    protected virtual void clientRagdollInterpolation()
    {
        double renderTime = NetworkManager.Singleton.NetworkTickSystem.ServerTime.TickWithPartial - interpolationDelay;
        if(poseCache.tryGetPosePair(renderTime, out RagdollPoseData start, out RagdollPoseData finish))
        {
            float percentage = (float)((renderTime - start.timeStamp)/(finish.timeStamp - start.timeStamp)); //Get percentage of interpolation
            hurtBoxes[0].transform.position = Vector3.Lerp(start.rootBonePosition, finish.rootBonePosition, percentage);
            for(int i = 0; i < hurtBoxes.Length; i++)
            {
                hurtBoxes[i].transform.localRotation = Quaternion.Lerp(start.boneRotations[i], finish.boneRotations[i], percentage);
            }
        }
    }

    public virtual void FixedUpdate()
    {
        if(ragdolled.Value)
        {
            if(ragdolledPreviousFrame == false)
            {
                enterRagdoll();
                ragdolledPreviousFrame = true;
            }
        }
        if(!ragdolled.Value)
        {
            if(ragdolledPreviousFrame == true)
            {
                exitRagdoll();
                ragdolledPreviousFrame = false;
            }
        }
    }

    public virtual void exitRagdollConditions()
    {
        if(ragdolled.Value)
        {
            if(hipBoneRB.velocity.magnitude < 0.5f && !health.dead.Value)
            {
                elapsedGetUpTime += Time.deltaTime;
            }
            else
            {
                elapsedGetUpTime = 0f;
            }

            if(elapsedGetUpTime >= getUpTime)
            {
                ragdolled.Value = false;
                elapsedGetUpTime = 0f;
            }
        }
        
    }

    public virtual void enterRagdoll()
    {
        foreach(System.Action i in actionsOnEnteringRagdoll)
        {
            i.Invoke();
        }
        animator.source.enabled = false;
        audioPlayer.playAudio(enterRagdollClip.name);

        //Server side physics code
        if(IsServer || health.dead.Value)
        {
            foreach(var hurtbox in hurtBoxes)
            {
                hurtbox.setRBRagdoll();
                knockback.transferKnockbackToRagdoll(hurtbox.rb, hurtbox.positionDelta);
            }
        }
        
        colliderComponent.enabled = false;
    }

    public virtual void exitRagdoll()
    {
        foreach(System.Action i in actionsOnExitingRagdoll)
        {
            i.Invoke();
        }

        characterRepositionRagdollState();

        animator.source.enabled = true;
        colliderComponent.enabled = true;

        //Server side physics code
        if(IsServer)
        {
            foreach(var hurtbox in hurtBoxes)
            {
                hurtbox.setRBKinematic();
            }
        }

        foreach(var hurtbox in hurtBoxes)
        {
            hurtbox.resetToRest();
        }
    }

    public virtual void characterRepositionRagdollState()
    {
        RaycastHit hit;
        if (Physics.Raycast(hipBoneRB.position, Vector3.down, out hit, 1f, groundMask))
        {
            transform.position = hit.point + Vector3.up;
        }
        else
        {
            transform.position = hipBone.transform.position + Vector3.up * 0.5f;
        }
    }

    /// <summary>
    /// Actions are run on both server and client
    /// </summary>
    public void addActionUponEnteringRagdoll(System.Action action)
    {
        Debug.Log($"Action added on ragdoll");
        actionsOnEnteringRagdoll.Add(action);
    }

    /// <summary>
    /// Actions are run on both server and client
    /// </summary>
    public void addActionUponExitingRagdoll(System.Action action)
    {
        Debug.Log($"Action added on ragdoll exit");
        actionsOnExitingRagdoll.Add(action);
    }

    [ServerRpc]
    public void setRagdollStateServerRpc(bool value)
    {
        setRagdollState(value);
    }

    public void setRagdollState(bool value)
    {
        if(IsServer)
        {
            ragdolled.Value = value;
        }
    }
}
