﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.Toolkit.Core.Definitions.Physics;
using Microsoft.MixedReality.Toolkit.Core.Definitions.Utilities;
using Microsoft.MixedReality.Toolkit.Core.Devices.Hands;
using Microsoft.MixedReality.Toolkit.Core.Interfaces.Devices;
using Microsoft.MixedReality.Toolkit.Core.Interfaces.InputSystem;
using Microsoft.MixedReality.Toolkit.Core.Services;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.SDK.UX.Pointers
{
    public class SpherePointer : BaseControllerPointer, IMixedRealityNearPointer
    {
        private RaycastMode raycastMode = RaycastMode.SphereColliders;

        public override RaycastMode RaycastMode { get { return raycastMode; } set { raycastMode = value; } }

        [SerializeField]
        private bool debugMode = false;

        private Transform debugSphere;

        /// <summary>
        /// Currently performs a sphere check.
        /// Currently anything that has a collider is considered "Grabbable".
        /// Eventually we need to filter based on things that can respond
        /// to grab events.
        /// </summary>
        /// <returns>True if the hand is near anything that's grabbable.</returns>
        public bool IsNearObject
        {
            get
            {
                Vector3 position;
                if (TryGetNearGraspPoint(out position))
                {
                    return Physics.CheckSphere(position, SphereCastRadius + 0.05f, ~Physics.IgnoreRaycastLayer);
                }

                return false;
            }
        }

        /// <inheritdoc />
        public override void OnPreRaycast()
        {
            if (Rays == null)
            {
                Rays = new RayStep[1];
            }

            Vector3 pointerPosition;
            if (TryGetNearGraspPoint(out pointerPosition))
            {
                if (debugMode)
                {
                    if (debugSphere == null)
                    {
                        debugSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere).transform;
                        debugSphere.localScale = Vector3.one * SphereCastRadius * 2;
                        Destroy(debugSphere.gameObject.GetComponent<Collider>());
                    }

                    debugSphere.position = pointerPosition;
                }

                Vector3 endPoint = Vector3.forward * SphereCastRadius;
                Rays[0].UpdateRayStep(ref pointerPosition, ref endPoint);
            }
        }

        /// <summary>
        /// Gets the position of where grasp happens
        /// For sixdof it's just the pointer origin
        /// for hand it's the average of index and thumb.
        /// </summary>
        public bool TryGetNearGraspPoint(out Vector3 result)
        {
            // For now, assume that anything that is a sphere pointer is a hand
            // TODO: have a way to determine if this is a fully articulated hand and return 
            // ray origin if it's a sixdof
            if (Controller != null && Controller is IMixedRealityHand)
            {
                HandJointUtils.TryGetJointPose(TrackedHandJoint.IndexTip, Controller.ControllerHandedness, out MixedRealityPose index);
                HandJointUtils.TryGetJointPose(TrackedHandJoint.ThumbTip, Controller.ControllerHandedness, out MixedRealityPose thumb);
                if (index != null && thumb != null)
                {
                    // result = 0.5f * (index.position + thumb.position);
                    result = index.Position;
                    return true;
                }
            }
            else if (TryGetPointerPosition(out result))
            {
                return true;
            }

            result = Vector3.zero;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetDistanceToNearestSurface(out float distance)
        {
            var focusProvider = MixedRealityToolkit.InputSystem?.FocusProvider;
            if (focusProvider != null)
            {
                FocusDetails focusDetails;
                if (focusProvider.TryGetFocusDetails(this, out focusDetails))
                {
                    distance = focusDetails.RayDistance;
                }
            }

            distance = 0.0f;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetNormalToNearestSurface(out Vector3 normal)
        {
            var focusProvider = MixedRealityToolkit.InputSystem?.FocusProvider;
            if (focusProvider != null)
            {
                FocusDetails focusDetails;
                if (focusProvider.TryGetFocusDetails(this, out focusDetails))
                {
                    normal = focusDetails.Normal;
                }
            }

            normal = Vector3.forward;
            return false;
        }

        private void OnDestroy()
        {
            if (debugSphere)
            {
                Destroy(debugSphere.gameObject);
            }
        }
    }
}