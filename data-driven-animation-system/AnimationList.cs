using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NewAnimationSet", menuName = "CharacterAnimation/Create Animation Set", order = 0)]
public class AnimationList : ScriptableObject
{
    public List<AnimationClip> animations = new List<AnimationClip>();

    [SerializeField] Dictionary<int, AnimationClip> hashTable;

    public AnimationClip getAnimationFromHash(int hash)
    {
        hashTable.TryGetValue(hash, out AnimationClip result);
        if(result == null)
        {
            Debug.LogError("Hash " + hash + " does not match any animation (Are you using the correct animation set?)");
        }
        return result;
    }

    void OnEnable()
    {
        if(hashTable == null)
        {
            hashTable = new Dictionary<int, AnimationClip>();
        }
        foreach(AnimationClip i in animations)
        {
            hashTable[Animator.StringToHash(i.name)] = i;
        }
    }
}
