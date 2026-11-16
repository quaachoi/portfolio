using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Playables;
using UnityEngine.Animations;

public struct SnapshotInterpreterJob : IAnimationJob
{
    public bool hasPose;

    public NativeArray<TransformStreamHandle> handles;

    //These arrays serve as the snapshotted pose
    public NativeArray<Vector3> positions;
    public NativeArray<Quaternion> rotations;
    public NativeArray<Vector3> scales;

    public void Initialize(Animator animator, Transform[] bones)
    {
        handles = new NativeArray<TransformStreamHandle>(bones.Length, Allocator.Persistent);
        positions = new NativeArray<Vector3>(bones.Length, Allocator.Persistent);
        rotations = new NativeArray<Quaternion>(bones.Length, Allocator.Persistent);
        scales = new NativeArray<Vector3>(bones.Length, Allocator.Persistent);

        for(int i = 0; i < bones.Length; i++)
        {
            handles[i] = animator.BindStreamTransform(bones[i]);
        }
    }

    public void SetPose(NativeArray<Vector3> _positions,
                        NativeArray<Quaternion> _rotations,
                        NativeArray<Vector3> _scales)
    {
        for(int i = 0; i < handles.Length; i++)
        {
            positions[i] = _positions[i];
            rotations[i] = _rotations[i];
            scales[i] = _scales[i];
        }
        hasPose = true;
    }

    public void ProcessAnimation(AnimationStream stream)
    {
        if(!hasPose)
        {
            return;
        }
        // This method is called during the animation process pass.
        for(int i = 0; i < handles.Length; i++)
        {
            handles[i].SetLocalPosition(stream, positions[i]);
            // handles[i].SetLocalPosition(stream, positions[i] + (Vector3.up * 10));
            handles[i].SetLocalRotation(stream, rotations[i]);
            handles[i].SetLocalScale(stream, scales[i]);
        }
    }

    public void ProcessRootMotion(AnimationStream stream)
    {
        // This method is called during the root motion process pass.
    }

    public void Dispose()
    {
        if (handles.IsCreated)
        {
            handles.Dispose();
        }

        if (positions.IsCreated)
        {
            positions.Dispose();
        }

        if (rotations.IsCreated)
        {
            rotations.Dispose();
        }

        if (scales.IsCreated)
        {
            scales.Dispose();
        }
    }
}

public struct SnapshotCreatorJob : IAnimationJob
{
    // public bool writePose;

    public NativeArray<TransformStreamHandle> handles;

    //These arrays serve as the snapshotted pose
    public NativeArray<Vector3> positions;
    public NativeArray<Quaternion> rotations;
    public NativeArray<Vector3> scales;

    public void Initialize(Animator animator, Transform[] bones)
    {
        handles = new NativeArray<TransformStreamHandle>(bones.Length, Allocator.Persistent);
        positions = new NativeArray<Vector3>(bones.Length, Allocator.Persistent);
        rotations = new NativeArray<Quaternion>(bones.Length, Allocator.Persistent);
        scales = new NativeArray<Vector3>(bones.Length, Allocator.Persistent);

        for(int i = 0; i < bones.Length; i++)
        {
            handles[i] = animator.BindStreamTransform(bones[i]);
            scales[i] = Vector3.one;
        }
    }

    public void ProcessAnimation(AnimationStream stream)
    {
        // This method is called during the animation process pass.

        // if(writePose)
        // {
        //     for(int i = 0; i < handles.Length; i++)
        //     {
        //         positions[i] = handles[i].GetLocalPosition(stream);
        //         rotations[i] = handles[i].GetLocalRotation(stream);
        //         scales[i] = handles[i].GetLocalScale(stream);
        //         // rotations[i] = Quaternion.identity;
        //     }
        //     writePose = false;
        // }

        for(int i = 0; i < handles.Length; i++)
        {
            positions[i] = handles[i].GetLocalPosition(stream);
            rotations[i] = handles[i].GetLocalRotation(stream);
            scales[i] = handles[i].GetLocalScale(stream);
        }
    }

    public void ProcessRootMotion(AnimationStream stream)
    {
        // This method is called during the root motion process pass.
    }

    public void Dispose()
    {
        if (handles.IsCreated)
        {
            handles.Dispose();
        }

        if (positions.IsCreated)
        {
            positions.Dispose();
        }

        if (rotations.IsCreated)
        {
            rotations.Dispose();
        }

        if (scales.IsCreated)
        {
            scales.Dispose();
        }
    }
}

