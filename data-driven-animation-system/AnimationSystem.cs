using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public class AnimationSystem
{
    public Animator source;
    
    public PlayableGraph playableGraph;
    public readonly AnimationLayerMixerPlayable masterMixer;
    public readonly AnimatorControllerPlayable sourceAnimatorMixer; //Layer 0 (base animations)

    public AnimationSystem(Animator sourceAnimator)
    {
        source = sourceAnimator;

        playableGraph = PlayableGraph.Create("AnimationSystem");

        AnimationPlayableOutput playableOutput = AnimationPlayableOutput.Create(playableGraph, "Animation", sourceAnimator);

        //initialize playables
        masterMixer = AnimationLayerMixerPlayable.Create(playableGraph, 8);
        sourceAnimatorMixer = AnimatorControllerPlayable.Create(playableGraph, sourceAnimator.runtimeAnimatorController);

        playableOutput.SetSourcePlayable(masterMixer); //plug masterMixer into output

        masterMixer.ConnectInput(0, sourceAnimatorMixer, 0);

        playableGraph.GetRootPlayable(0).SetInputWeight(0, 1f);

        playableGraph.Play();
    }
    
    public IEnumerator blendInOutCoroutine(int layerID, float middleDuration, float blendInTime, float blendOutTime)
    {
        float timeElapsed = 0f;

        //Blend in
        while(timeElapsed < blendInTime)
        {
            float easedPercentage = Mathf.SmoothStep(0f, 1f, timeElapsed/blendInTime);
            masterMixer.SetInputWeight(layerID, easedPercentage);
            timeElapsed += Time.deltaTime;
            yield return null;
        }
        timeElapsed = 0;
        
        //Sustain
        while(timeElapsed < middleDuration)
        {
            timeElapsed += Time.deltaTime;
            masterMixer.SetInputWeight(layerID, 1);
            // Debug.Log("current weight: " + 1);
            yield return null;
        }
        timeElapsed = 0;

        //If the blend out time is zero, then don't reset the weight to zero
        if(blendOutTime != 0)
        {
            //Blend out
            while(timeElapsed < blendOutTime)
            {
                float easedPercentage = Mathf.SmoothStep(0f, 1f, timeElapsed/blendOutTime);
                masterMixer.SetInputWeight(layerID, 1 - easedPercentage);
                timeElapsed += Time.deltaTime;
                yield return null;
            }

            masterMixer.SetInputWeight(layerID, 0);
        }
    }

    public void Destroy()
    {
        if(playableGraph.IsValid())
        {
            playableGraph.Destroy();
        }
    }
}
