using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using UnityEngine.Playables;

public class ClipMixerPair
{
    public PlayableGraph graph;
    public Playable output;
    public AnimationClipPlayable currentClipPlayable;

    SnapshotCreatorJob snapshotCreatorJob;
    SnapshotInterpreterJob snapshotInterpreterJob;

    public AnimationScriptPlayable snapshotCreator;
    public AnimationScriptPlayable snapshotInterpreter;

    public AnimationMixerPlayable preSnapshotMixer;

    public IEnumerator blendCoroutine;

    /*
    The current animation playing is input zero
    The snapshotted pose of the previous animation blend is one
    Blending works by going from input one to zero
    */

    public ClipMixerPair(PlayableGraph _graph, Playable _output, Animator _animator, Transform[] _bones)
    {
        graph = _graph;

        //Initialize playables
        //Set up output
        output = _output;
        output.SetInputCount(1);

        //Set up custom jobs 
        snapshotCreatorJob = new SnapshotCreatorJob();
        snapshotCreatorJob.Initialize(_animator, _bones);

        snapshotInterpreterJob = new SnapshotInterpreterJob();
        snapshotInterpreterJob.Initialize(_animator, _bones);

        //Assign jobs to playables
        snapshotCreator = AnimationScriptPlayable.Create(graph, snapshotCreatorJob, 1);
        snapshotInterpreter = AnimationScriptPlayable.Create(graph, snapshotInterpreterJob);
        currentClipPlayable = AnimationClipPlayable.Create(graph, new AnimationClip());
        preSnapshotMixer = AnimationMixerPlayable.Create(graph, 2);

        //Connect all playables in this fashion:
        /*
        [currentClipPlayable] 0
                                >----- [AnimationMixer] ----- [snapshotCreator] ----- [output]
        [snapshotInterpreter] 1
        */

        output.ConnectInput(0, snapshotCreator, 0);
        snapshotCreator.ConnectInput(0, preSnapshotMixer, 0);
        preSnapshotMixer.ConnectInput(0, currentClipPlayable, 0);
        preSnapshotMixer.ConnectInput(1, snapshotInterpreter, 0);

        output.SetInputWeight(0, 1);
        preSnapshotMixer.SetInputWeight(0, 1);
        preSnapshotMixer.SetInputWeight(1, 0);
    }

    public void connectAndPlayAnimation(AnimationClip clip, float blendTime)
    {
        graph.GetOutput(0).SetSourcePlayable(graph.GetOutput(0).GetSourcePlayable()); //Prevent animation rigging from resetting weights to scene defaults (what the fuck???)

        //Let snapshot interpreter sample the snapshot creator's saved pose
        snapshotInterpreterJob.SetPose(snapshotCreatorJob.positions, snapshotCreatorJob.rotations, snapshotCreatorJob.scales);
        snapshotInterpreter.SetJobData(snapshotInterpreterJob);
        // graph.Evaluate();

        preSnapshotMixer.SetInputWeight(0, 0);
        preSnapshotMixer.SetInputWeight(1, 1);

        graph.Disconnect(preSnapshotMixer, 0);

        currentClipPlayable = AnimationClipPlayable.Create(graph, clip);
        preSnapshotMixer.ConnectInput(0, currentClipPlayable, 0);
        currentClipPlayable.Play();

        blendCoroutine = blendAnimation(blendTime);
    }

    public AnimationClip getCurrentClip()
    {
        if(currentClipPlayable.IsValid())
        {
            return currentClipPlayable.GetAnimationClip();
        }
        return null; 
    }

    public void pauseCurrentAnimation()
    {
        if(currentClipPlayable.IsValid())
        {
            currentClipPlayable.Pause();
        }
    }

    public void setCurrentAnimationSpeed(float speed)
    {
        currentClipPlayable.SetSpeed(speed);
    }

    public void unpauseCurrentAnimation()
    {
        if(currentClipPlayable.IsValid())
        {
            currentClipPlayable.Play();
        }
    }

    public void resetWeights()
    {
        preSnapshotMixer.SetInputWeight(0, 1);
        preSnapshotMixer.SetInputWeight(1, 0);
    }

    public void zeroWeights()
    {
        preSnapshotMixer.SetInputWeight(0, 0);
        preSnapshotMixer.SetInputWeight(1, 0);
    }

    IEnumerator blendAnimation(float duration)
    {
        float timeElapsed = 0;
        while(timeElapsed < duration)
        {
            float percentage = Mathf.SmoothStep(0f, 1f, timeElapsed/duration);
            
            preSnapshotMixer.SetInputWeight(0, percentage);
            preSnapshotMixer.SetInputWeight(1, 1 - percentage);

            timeElapsed += Time.deltaTime;
            yield return null; 
        }
        preSnapshotMixer.SetInputWeight(0, 1);
        preSnapshotMixer.SetInputWeight(1, 0);
    }

    public void Dispose()
    {
        if(snapshotCreator.IsValid())
        {
            snapshotCreator.Destroy();
        }

        if(snapshotInterpreter.IsValid())
        {
            snapshotInterpreter.Destroy();
        }

        snapshotCreatorJob.Dispose();
        snapshotInterpreterJob.Dispose();
    }
}

public class PlayableManager : NetworkBehaviour
{
    [System.Serializable]
    class ConstraintWeightCacher
    {
        public MultiAimConstraint constraint;
        [HideInInspector] public float cachedWeight;
    }
    [SerializeField] List<ConstraintWeightCacher> constraintList = new List<ConstraintWeightCacher>();

    protected Dictionary<int, Coroutine> layerBlendCoroutineIDList = new Dictionary<int, Coroutine>();
    protected Dictionary<int, Coroutine> animationSequenceCoroutineIDList = new Dictionary<int, Coroutine>();
    protected List<ClipMixerPair> layerOutputs = new List<ClipMixerPair>(); //Indices are layer indices (x+1, since layer zero is reserved for the animator)

    public Animator sourceAnimator;
    protected SkinnedMeshRenderer skinnedMesh;
    protected Transform[] bones;
    public AnimationSystem animSystem;
    public AnimationList animationSet;

    public void Start()
    {
        skinnedMesh = GetComponentInChildren<SkinnedMeshRenderer>();
        bones = new Transform[skinnedMesh.bones.Length];
        for(int i = 0; i < skinnedMesh.bones.Length; i++)
        {
            bones[i] = skinnedMesh.bones[i];
            Debug.Log(bones[i].name);
        }
        
        animSystem = new AnimationSystem(sourceAnimator);
    }

    public void Update()
    {
        animSystem.masterMixer.SetSpeed(Time.timeScale);
    }
    public void LateUpdate()
    {
        for(int i = 0; i < constraintList.Count; i++)
        {
            constraintList[i].cachedWeight = constraintList[i].constraint.weight;
        }
    }

    public void createClipMixerPair(Playable output)
    {
        layerOutputs.Add(new ClipMixerPair(animSystem.playableGraph, output, sourceAnimator, bones));
    }

    public AnimationClip getCurrentClip(int layerID)
    {
        return layerOutputs[layerID - 1].getCurrentClip(); 
    }

    public void blendInOutLocal(int layerID, float duration, float blendInPercentage, float blendOutPercentage)
    {
        stopBlend(layerID); //Stop any blends in progress if its the same layer
        Coroutine newCoroutine = StartCoroutine(animSystem.blendInOutCoroutine(layerID, duration, blendInPercentage, blendOutPercentage));
        layerBlendCoroutineIDList[layerID] = newCoroutine;
    }
    public void blendInOut(int layerID, float duration, float blendInPercentage, float blendOutPercentage)
    {
        if(!IsServer && IsOwner)
        {
            blendInOutLocal(layerID, duration, blendInPercentage, blendOutPercentage);
        }
        if(IsServer)
        {
            blendInOutClientRpc(layerID, duration, blendInPercentage, blendOutPercentage);
        }
        else if (IsClient)
        {
            blendInOutServerRpc(layerID, duration, blendInPercentage, blendOutPercentage);
        }
    }
    [ServerRpc]
    void blendInOutServerRpc(int layerID, float duration, float blendInPercentage, float blendOutPercentage)
    {
        blendInOutClientRpc(layerID, duration, blendInPercentage, blendOutPercentage);
    }
    [ClientRpc]
    void blendInOutClientRpc(int layerID, float duration, float blendInPercentage, float blendOutPercentage)
    {
        if(IsOwner && !IsServer)
        {
            return;
        }
        blendInOutLocal(layerID, duration, blendInPercentage, blendOutPercentage);
    }
    public void stopBlend(int layerID)
    {
        if(layerBlendCoroutineIDList.TryGetValue(layerID, out Coroutine running))
        {
            StopCoroutine(running);
            layerBlendCoroutineIDList.Remove(layerID);
        }
    }


    public void playAnimationLocal(int layerID, int hash, float blendTime, bool repeatable = true, bool loop = false)
    {
        if(layerID < 1)
        {
            Debug.LogError("Failed to play animation, layer zero is reserved for base animator!");
            return;
        }
        if(!repeatable && layerOutputs[layerID - 1].getCurrentClip() == animationSet.getAnimationFromHash(hash))
        {
            return;
        }

        stopAnimationSequence(layerID);

        if(layerOutputs[layerID - 1].blendCoroutine != null)
        {
            StopCoroutine(layerOutputs[layerID - 1].blendCoroutine);
            layerOutputs[layerID - 1].resetWeights();
        }

        layerOutputs[layerID - 1].connectAndPlayAnimation(animationSet.getAnimationFromHash(hash), blendTime);

        StartCoroutine(layerOutputs[layerID - 1].blendCoroutine);

        for(int i = 0; i < constraintList.Count; i++)
        {
            constraintList[i].constraint.weight = constraintList[i].cachedWeight;
        }
    }
    public void playAnimation(int layerID, AnimationClip clip, float blendTime, bool repeatable = true, bool loop = false)
    {
        int hash = Animator.StringToHash(clip.name);
        if(!IsServer && IsOwner)
        {
            playAnimationLocal(layerID, hash, blendTime, repeatable, loop);
        }
        if(IsServer)
        {
            playAnimationClientRpc(layerID, hash, blendTime, repeatable, loop);
        }
        else if (IsClient)
        {
            playAnimationServerRpc(layerID, hash, blendTime, repeatable, loop);
        }
    }
    public void playAnimation(int layerID, int hash, float blendTime, bool repeatable = true, bool loop = false)
    {
        if(!IsServer && IsOwner)
        {
            playAnimationLocal(layerID, hash, blendTime, repeatable, loop);
        }
        if(IsServer)
        {
            playAnimationClientRpc(layerID, hash, blendTime, repeatable, loop);
        }
        else if (IsClient)
        {
            playAnimationServerRpc(layerID, hash, blendTime, repeatable, loop);
        }
    }
    [ServerRpc]
    void playAnimationServerRpc(int layerID, int hash, float blendTime, bool repeatable = true, bool loop = false)
    {
        playAnimationClientRpc(layerID, hash, blendTime, repeatable, loop);
    }
    [ClientRpc]
    void playAnimationClientRpc(int layerID, int hash, float blendTime, bool repeatable = true, bool loop = false)
    {
        if(IsOwner && !IsServer)
        {
            return;
        }
        playAnimationLocal(layerID, hash, blendTime, repeatable, loop);
    }

    IEnumerator animationSequenceCoroutine(int layerID, int hash1, int hash2, float blendTime)
    {
        void startSequence(int layerID, int hash, float blendTime)
        {
            if(layerID < 1)
            {
                Debug.LogError("Failed to play animation, layer zero is reserved for base animator!");
                return;
            }
            if(layerOutputs[layerID - 1].getCurrentClip() == animationSet.getAnimationFromHash(hash))
            {
                return;
            }

            if(layerOutputs[layerID - 1].blendCoroutine != null)
            {
                StopCoroutine(layerOutputs[layerID - 1].blendCoroutine);
                layerOutputs[layerID - 1].resetWeights();
            }

            layerOutputs[layerID - 1].connectAndPlayAnimation(animationSet.getAnimationFromHash(hash), blendTime);

            StartCoroutine(layerOutputs[layerID - 1].blendCoroutine);
        }

        // void startSecondSequence(int layerID, int hash)
        // {
        //     if(layerID < 1)
        //     {
        //         Debug.LogError("Failed to play animation, layer zero is reserved for base animator!");
        //         return;
        //     }
        //     layerOutputs[layerID - 1].connectAndPlayAnimationNoBlend(animationSet.getAnimationFromHash(hash));
        // }

        startSequence(layerID, hash1, blendTime);
        float time = animationSet.getAnimationFromHash(hash1).length;
        yield return new WaitForSeconds(time);
        startSequence(layerID, hash2, 0.3f);
    }

    public void playAnimationSeq(int layerID, AnimationClip clip1, AnimationClip clip2, float blendTime)
    {
        int hash1 = Animator.StringToHash(clip1.name);
        int hash2 = Animator.StringToHash(clip2.name);
        if(!IsServer && IsOwner)
        {
            playAnimationSeqLocal(layerID, hash1, hash2, blendTime);
        }
        if(IsServer)
        {
            playAnimationSeqClientRpc(layerID, hash1, hash2, blendTime);
        }
        else if (IsClient)
        {
            playAnimationSeqServerRpc(layerID, hash1, hash2, blendTime);
        }
    }
    public void playAnimationSeq(int layerID, int hash1, int hash2, float blendTime)
    {
        if(!IsServer && IsOwner)
        {
            playAnimationSeqLocal(layerID, hash1, hash2, blendTime);
        }
        if(IsServer)
        {
            playAnimationSeqClientRpc(layerID, hash1, hash2, blendTime);
        }
        else if (IsClient)
        {
            playAnimationSeqServerRpc(layerID, hash1, hash2, blendTime);
        }
    }
    public void playAnimationSeqLocal(int layerID, int hash1, int hash2, float blendTime)
    {
        stopAnimationSequence(layerID);
        Coroutine newCoroutine = StartCoroutine(animationSequenceCoroutine(layerID, hash1, hash2, blendTime));
        animationSequenceCoroutineIDList[layerID] = newCoroutine;
    }
    [ServerRpc]
    void playAnimationSeqServerRpc(int layerID, int hash1, int hash2, float blendTime)
    {
        playAnimationSeqClientRpc(layerID, hash1, hash2, blendTime);
    }
    [ClientRpc]
    void playAnimationSeqClientRpc(int layerID, int hash1, int hash2, float blendTime)
    {
        if(IsOwner && !IsServer)
        {
            return;
        }
        playAnimationSeqLocal(layerID, hash1, hash2, blendTime);
    }
    public void stopAnimationSequence(int layerID)
    {
        if(animationSequenceCoroutineIDList.TryGetValue(layerID, out Coroutine running))
        {
            StopCoroutine(running);
            animationSequenceCoroutineIDList.Remove(layerID);
        }
    }

    public void setCurrentAnimationSpeedLocal(int layerID, float speed)
    {
        layerOutputs[layerID].setCurrentAnimationSpeed(speed);
    }

    public void pauseAnimationLocal(int layerID)
    {
        layerOutputs[layerID].pauseCurrentAnimation();
    }
    public void pauseAnimation(int layerID)
    {
        if(!IsServer && IsOwner)
        {
            pauseAnimationLocal(layerID);
        }
        if(IsServer)
        {
            pauseAnimationClientRpc(layerID);
        }
        else if (IsClient)
        {
            pauseAnimationServerRpc(layerID);
        }
    }
    [ServerRpc]
    public void pauseAnimationServerRpc(int layerID)
    {
        pauseAnimationClientRpc(layerID);
    }
    [ClientRpc]
    public void pauseAnimationClientRpc(int layerID)
    {
        if(IsOwner && !IsServer)
        {
            return;
        }
        pauseAnimationLocal(layerID);
    }

    public void OnDestroy()
    {
        foreach(var i in layerOutputs)
        {
            i.Dispose();
        }
        animSystem.Destroy();
    }
}