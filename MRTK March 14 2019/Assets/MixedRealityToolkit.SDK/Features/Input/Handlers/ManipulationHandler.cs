// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using UnityEngine;
using System.Linq;
using UnityEngine.Assertions;
using System.Collections.Generic;
using Microsoft.MixedReality.Toolkit.Core.Attributes;
using Microsoft.MixedReality.Toolkit.Core.EventDatum.Input;
using Microsoft.MixedReality.Toolkit.Core.Definitions.Utilities;
using Microsoft.MixedReality.Toolkit.Core.Utilities.Physics;
using Microsoft.MixedReality.Toolkit.Core.Interfaces.Devices;
using Microsoft.MixedReality.Toolkit.Core.Interfaces.InputSystem.Handlers;
using Microsoft.MixedReality.Toolkit.Core.Interfaces.InputSystem;
using Microsoft.MixedReality.Toolkit.Core.Definitions.Devices;
using Microsoft.MixedReality.Toolkit.Core.Services;
using Microsoft.MixedReality.Toolkit.SDK.Input.Events;

namespace Microsoft.MixedReality.Toolkit.Examples.Demos
{
    /// <summary>
    /// This script allows for an object to be movable, scalable, and rotatable with one or two hands. 
    /// You may also configure the script on only enable certain manipulations. The script works with 
    /// both HoloLens' gesture input and immersive headset's motion controller input.
    /// See Assets/HoloToolkit-Examples/Input/Readme/README_TwoHandManipulationTest.md
    /// for instructions on how to use the script.
    /// </summary>
    /// 
    public class ManipulationHandler : MonoBehaviour, IMixedRealityPointerHandler, IMixedRealityFocusChangedHandler
    {
        #region Public Enums
        public enum HandMovementType
        {
            OneHandedOnly = 0,
            TwoHandedOnly,
            OneAndTwoHanded
        }
        public enum TwoHandedManipulation
        {
            Scale,
            Rotate,
            MoveScale,
            RotateScale,
            MoveRotateScale
        };
        public enum RotateInOneHandType
        {
            DoNotRotateInOneHand,
            RotateAboutObjectCenter,
            RotateAboutGrabPoint
        };
        [System.Flags]
        public enum ReleaseBehaviorType
        {
            KeepVelocity = 1 << 0,
            KeepAngularVelocity = 1 << 1
        }
        #endregion Public Enums

        #region Serialized Fields

        [SerializeField]
        [Tooltip("Transform that will be dragged. Defaults to the object of the component.")]
        private Transform hostTransform = null;

        public Transform HostTransform => hostTransform;

        [Header("Manipulation")]
        [SerializeField]
        [Tooltip("Can manipulation be done only with one hand, only with two hands, or with both?")]
        private HandMovementType manipulationType = HandMovementType.OneAndTwoHanded;

        public HandMovementType ManipulationType => manipulationType;

        [SerializeField]
        [Tooltip("What manipulation will two hands perform?")]
        private TwoHandedManipulation twoHandedManipulationType = TwoHandedManipulation.MoveRotateScale;

        public TwoHandedManipulation TwoHandedManipulationType => twoHandedManipulationType;

        [SerializeField]
        [Tooltip("Specifies whether manipulation can be done using far interaction with pointers.")]
        private bool allowFarManipulation = true;

        public bool AllowFarManipulation => allowFarManipulation;

        [SerializeField]
        private RotateInOneHandType oneHandRotationMode = RotateInOneHandType.RotateAboutGrabPoint;

        [SerializeField]
        [EnumFlags]
        [Tooltip("Rigid body behavior of the dragged object when releasing it.")]
        private ReleaseBehaviorType releaseBehavior = ReleaseBehaviorType.KeepVelocity | ReleaseBehaviorType.KeepAngularVelocity;

        public ReleaseBehaviorType ReleaseBehavior => releaseBehavior;

        [Header("Constraints")]
        [SerializeField]
        [Tooltip("Constrain rotation along an axis")]
        private RotationConstraintType constraintOnRotation = RotationConstraintType.None;

        public RotationConstraintType ConstraintOnRotation => constraintOnRotation;

        [SerializeField]
        [Tooltip("Constrain movement")]
        private MovementConstraintType constraintOnMovement = MovementConstraintType.None;

        public MovementConstraintType ConstraintOnMovement => constraintOnMovement;

        [Header("Smoothing")]
        [SerializeField]
        [Tooltip("Check to enable frame-rate independent smoothing. ")]
        private bool smoothingActive = true;

        public bool SmoothingActive => smoothingActive;

        [SerializeField]
        [Range(0, 1)]
        [Tooltip("Enter amount representing amount of smoothing to apply to the movement, scale, rotation.  Smoothing of 0 means no smoothing. Max value means no change to value.")]
        private float smoothingAmountOneHandManip = 0.001f;

        public float SmoothingAmoutOneHandManip => smoothingAmountOneHandManip;

        #endregion Serialized Fields

        #region Event handlers
        public ManipulationEvent OnManipulationEnded;
        public ManipulationEvent OnManipulationStarted;
        #endregion

        #region Private Properties

        [System.Flags]
        private enum State
        {
            Start = 0x000,
            Moving = 0x001,
            Scaling = 0x010,
            Rotating = 0x100,
            MovingScaling = Moving | Scaling,
            RotatingScaling = Rotating | Scaling,
            MovingRotatingScaling = Moving | Rotating | Scaling
        };

        private State currentState = State.Start;
        private TwoHandMoveLogic m_moveLogic;
        private TwoHandScaleLogic m_scaleLogic;
        private TwoHandRotateLogic m_rotateLogic;
        private Dictionary<uint, IMixedRealityPointer> pointerIdToPointerMap = new Dictionary<uint, IMixedRealityPointer>();

        private Quaternion objectToHandRotation;
        private Vector3 objectToHandTranslation;
        private bool isNearManipulation;
        // This can probably be consolidated so that we use same for one hand and two hands
        private Quaternion targetRotationTwoHands;

        private Rigidbody rigidBody;
        private bool wasKinematic = false;

        #endregion

        #region MonoBehaviour Functions
        private void Awake()
        {
            m_moveLogic = new TwoHandMoveLogic(constraintOnMovement);
            m_rotateLogic = new TwoHandRotateLogic(constraintOnRotation);
            m_scaleLogic = new TwoHandScaleLogic();
        }
        private void Start()
        {
            if (hostTransform == null)
            {
                hostTransform = transform;
            }
        }
        private void Update()
        {
            if (currentState != State.Start)
            {
                UpdateStateMachine();
            }
        }
        #endregion MonoBehaviour Functions

        #region Private Methods
        private bool TryGetGripPositionAndOrientation(IMixedRealityPointer pointer, out Quaternion orientation, out Vector3 position)
        {
            var handController = pointer.Controller as IMixedRealityHand;
            if (handController != null)
            {
                if (handController.TryGetJoint(TrackedHandJoint.Palm, out MixedRealityPose palm) &&
                    handController.TryGetJoint(TrackedHandJoint.IndexTip, out MixedRealityPose tip))
                {
                    orientation = palm.Rotation;
                    position = tip.Position;
                    return true;
                }
            }
            orientation = Quaternion.identity;
            position = Vector3.zero;
            return false;
        }
        private void SetManipulationMode(TwoHandedManipulation mode)
        {
            twoHandedManipulationType = mode;
        }
        private Vector3 GetPointersCentroid()
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (var p in pointerIdToPointerMap.Values)
            {
                Vector3 current = Vector3.zero;
                if (p.TryGetPointerPosition(out current))
                {
                    sum += current;
                    count++;
                }
            }
            return sum / Math.Max(1, count);
        }

        private Vector3 GetPointersVelocity()
        {
            Vector3 sum = Vector3.zero;
            foreach (var p in pointerIdToPointerMap.Values)
            {
                sum += p.Controller.Velocity;
            }
            return sum / Math.Max(1, pointerIdToPointerMap.Count);
        }

        private Vector3 GetPointersAngularVelocity()
        {
            Vector3 sum = Vector3.zero;
            foreach (var p in pointerIdToPointerMap.Values)
            {
                sum += p.Controller.AngularVelocity;
            }
            return sum / Math.Max(1, pointerIdToPointerMap.Count);
        }

        private bool IsNearManipulation()
        {
            foreach (var item in pointerIdToPointerMap)
            {
                if (item.Value is IMixedRealityNearPointer)
                {
                    return true;
                }
            }
            return false;
        }
        private void UpdateStateMachine()
        {
            var handsPressedCount = pointerIdToPointerMap.Count;
            State newState = currentState;
            switch (currentState)
            {
                case State.Start:
                case State.Moving:
                    if (handsPressedCount == 0)
                    {
                        newState = State.Start;
                    }
                    else if (handsPressedCount == 1 && manipulationType != HandMovementType.TwoHandedOnly)
                    {
                        newState = State.Moving;
                    }
                    else if (handsPressedCount > 1 && manipulationType != HandMovementType.OneHandedOnly)
                    {
                        switch (twoHandedManipulationType)
                        {
                            case TwoHandedManipulation.Scale:
                                newState = State.Scaling;
                                break;
                            case TwoHandedManipulation.Rotate:
                                newState = State.Rotating;
                                break;
                            case TwoHandedManipulation.MoveScale:
                                newState = State.MovingScaling;
                                break;
                            case TwoHandedManipulation.RotateScale:
                                newState = State.RotatingScaling;
                                break;
                            case TwoHandedManipulation.MoveRotateScale:
                                newState = State.MovingRotatingScaling;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    break;
                case State.Scaling:
                case State.Rotating:
                case State.MovingScaling:
                case State.RotatingScaling:
                case State.MovingRotatingScaling:
                    // TODO: if < 2, make this go to start state ('drop it')
                    if (handsPressedCount == 0)
                    {
                        newState = State.Start;
                    }
                    else if (handsPressedCount == 1)
                    {
                        newState = State.Moving;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            InvokeStateUpdateFunctions(currentState, newState);
            currentState = newState;
        }
        private void InvokeStateUpdateFunctions(State oldState, State newState)
        {
            if (newState != oldState)
            {
                switch (newState)
                {
                    case State.Moving:
                        HandleOneHandMoveStarted();
                        break;
                    case State.Start:
                        HandleManipulationEnded();
                        break;
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.Scaling:
                    case State.Rotating:
                    case State.MovingScaling:
                        HandleTwoHandManipulationStarted(newState);
                        break;
                }
                switch (oldState)
                {
                    case State.Start:
                        HandleManipulationStarted();
                        break;
                    case State.Scaling:
                    case State.Rotating:
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.MovingScaling:
                        HandleTwoHandManipulationEnded();
                        break;
                }
            }
            else
            {
                switch (newState)
                {
                    case State.Moving:
                        HandleOneHandMoveUpdated();
                        break;
                    case State.Scaling:
                    case State.Rotating:
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.MovingScaling:
                        HandleTwoHandManipulationUpdated();
                        break;
                    default:
                        break;
                }
            }
        }
        #endregion Private Methods

        #region Hand Event Handlers
        private bool IsEventAGrabInteraction(MixedRealityPointerEventData eventData)
        {
            return eventData.MixedRealityInputAction.Description == "Grip Press" || eventData.MixedRealityInputAction.Description == "Select";
        }

        private MixedRealityInteractionMapping GetSpatialGripInfoForController(IMixedRealityController controller)
        {
            if (controller == null)
            {
                return null;
            }

            return controller.Interactions?.First(x => x.InputType == DeviceInputType.SpatialGrip);
        }

        /// <inheritdoc />
        public void OnPointerDown(MixedRealityPointerEventData eventData)
        {
            if (manipulationType == HandMovementType.OneHandedOnly && pointerIdToPointerMap.Count > 0)
            {
                // If we only allow one hand manipulations, then ignore the second hand completely
                return;
            }
            if (!allowFarManipulation && eventData.Pointer as IMixedRealityNearPointer == null)
            {
                return;
            }
            uint id = eventData.Pointer.PointerId;
            // Ignore poke pointer events
            if (!eventData.used
                && IsEventAGrabInteraction(eventData)
                && !pointerIdToPointerMap.ContainsKey(eventData.Pointer.PointerId))
            {
                Vector3 position;
                if (eventData.Pointer.TryGetPointerPosition(out position))
                {
                    if (pointerIdToPointerMap.Count == 0)
                    {
                        rigidBody = GetComponent<Rigidbody>();
                        if (rigidBody != null)
                        {
                            wasKinematic = rigidBody.isKinematic;
                            rigidBody.isKinematic = true;
                        }
                    }
                    pointerIdToPointerMap.Add(id, eventData.Pointer);
                }

                UpdateStateMachine();
                eventData.Use();
            }
        }

        /// <inheritdoc />
        public void OnPointerUp(MixedRealityPointerEventData eventData)
        {
            uint id = eventData.Pointer.PointerId;
            if (pointerIdToPointerMap.ContainsKey(id))
            {
                if (pointerIdToPointerMap.Count == 1 && rigidBody != null)
                {
                    rigidBody.isKinematic = wasKinematic;

                    if (releaseBehavior.HasFlag(ReleaseBehaviorType.KeepVelocity))
                    {
                        rigidBody.velocity = GetPointersVelocity();
                    }

                    if (releaseBehavior.HasFlag(ReleaseBehaviorType.KeepAngularVelocity))
                    {
                        rigidBody.angularVelocity = GetPointersAngularVelocity();
                    }

                    rigidBody = null;
                }

                pointerIdToPointerMap.Remove(id);
            }

            UpdateStateMachine();
            eventData.Use();
        }
        #endregion Hand Event Handlers

        #region Private Event Handlers
        private void HandleTwoHandManipulationUpdated()
        {
            var targetPosition = hostTransform.position;
            var targetScale = hostTransform.localScale;

            if ((currentState & State.Moving) > 0)
            {
                targetPosition = m_moveLogic.Update(GetPointersCentroid(), IsNearManipulation());
            }

            var handPositionMap = GetHandPositionMap();

            if ((currentState & State.Rotating) > 0)
            {
                targetRotationTwoHands = m_rotateLogic.Update(handPositionMap, targetRotationTwoHands);
            }
            if ((currentState & State.Scaling) > 0)
            {
                targetScale = m_scaleLogic.UpdateMap(handPositionMap);
            }

            float lerpAmount = GetLerpAmount();
            hostTransform.position = Vector3.Lerp(hostTransform.position, targetPosition, lerpAmount);
            // Currently the two hand rotation algorithm doesn't allow for lerping, but it should. Fix this.
            hostTransform.rotation = Quaternion.Lerp(hostTransform.rotation, targetRotationTwoHands, lerpAmount);
            hostTransform.localScale = Vector3.Lerp(hostTransform.localScale, targetScale, lerpAmount);
        }

        private void HandleOneHandMoveUpdated()
        {
            Debug.Assert(pointerIdToPointerMap.Count == 1);
            IMixedRealityPointer pointer = pointerIdToPointerMap.Values.First();

            var interactionMapping = GetSpatialGripInfoForController(pointer.Controller);
            if (interactionMapping != null)
            {
                Quaternion targetRotation = Quaternion.identity;
                if (oneHandRotationMode == RotateInOneHandType.DoNotRotateInOneHand)
                {
                    targetRotation = hostTransform.rotation;
                }
                else
                {
                    targetRotation = interactionMapping.PoseData.Rotation * objectToHandRotation;
                    switch (constraintOnRotation)
                    {
                        case RotationConstraintType.XAxisOnly:
                            targetRotation.eulerAngles = Vector3.Scale(targetRotation.eulerAngles, Vector3.right);
                            break;
                        case RotationConstraintType.YAxisOnly:
                            targetRotation.eulerAngles = Vector3.Scale(targetRotation.eulerAngles, Vector3.up);
                            break;
                        case RotationConstraintType.ZAxisOnly:
                            targetRotation.eulerAngles = Vector3.Scale(targetRotation.eulerAngles, Vector3.forward);
                            break;
                    }
                }

                Vector3 targetPosition;
                if (IsNearManipulation())
                {
                    if (oneHandRotationMode == RotateInOneHandType.RotateAboutGrabPoint)
                    {
                        targetPosition = (interactionMapping.PoseData.Rotation * objectToHandTranslation) + interactionMapping.PoseData.Position;
                    }
                    else // RotateAboutCenter or DoNotRotateInOneHand
                    {
                        targetPosition = objectToHandTranslation + interactionMapping.PoseData.Position;
                    }
                }
                else
                {
                    targetPosition = m_moveLogic.Update(GetPointersCentroid(), IsNearManipulation());
                }

                float lerpAmount = GetLerpAmount();
                Quaternion smoothedRotation = Quaternion.Lerp(hostTransform.rotation, targetRotation, lerpAmount);
                Vector3 smoothedPosition = Vector3.Lerp(hostTransform.position, targetPosition, lerpAmount);
                hostTransform.SetPositionAndRotation(smoothedPosition, smoothedRotation);
            }
        }

        private void HandleTwoHandManipulationStarted(State newState)
        {
            var handPositionMap = GetHandPositionMap();
            targetRotationTwoHands = hostTransform.rotation;

            if ((newState & State.Rotating) > 0)
            {
                m_rotateLogic.Setup(handPositionMap);
            }
            if ((newState & State.Moving) > 0)
            {
                m_moveLogic.Setup(GetPointersCentroid(), hostTransform.position);
            }
            if ((newState & State.Scaling) > 0)
            {
                m_scaleLogic.Setup(handPositionMap, hostTransform);
            }
        }
        private void HandleTwoHandManipulationEnded() { }

        private void HandleOneHandMoveStarted()
        {
            Assert.IsTrue(pointerIdToPointerMap.Count == 1);
            IMixedRealityPointer pointer = pointerIdToPointerMap.Values.First();

            m_moveLogic.Setup(GetPointersCentroid(), hostTransform.position);

            var interactionMapping = GetSpatialGripInfoForController(pointer.Controller);
            if (interactionMapping != null)
            {
                // Calculate relative transform from object to hand.
                Quaternion worldToPalmRotation = Quaternion.Inverse(interactionMapping.PoseData.Rotation);
                objectToHandRotation = worldToPalmRotation * hostTransform.rotation;
                objectToHandTranslation = (hostTransform.position - interactionMapping.PoseData.Position);
                if (oneHandRotationMode == RotateInOneHandType.RotateAboutGrabPoint)
                {
                    objectToHandTranslation = worldToPalmRotation * objectToHandTranslation;
                }
            }
        }

        private void HandleManipulationStarted()
        {
            isNearManipulation = IsNearManipulation();
            // TODO: If we are on Baraboo, push and pop modal input handler so that we can use old ggv manipulation
            // for Sydney, we don't want to do this
            OnManipulationStarted.Invoke(new ManipulationEventData { IsNearInteraction = isNearManipulation });

        }
        private void HandleManipulationEnded()
        {
            // TODO: If we are on Baraboo, push and pop modal input handler so that we can use old ggv manipulation
            // for Sydney, we don't want to do this
            OnManipulationEnded.Invoke(new ManipulationEventData { IsNearInteraction = isNearManipulation });

        }
        #endregion Private Event Handlers

        #region Unused Event Handlers

        /// <inheritdoc />
        public void OnInputPressed(InputEventData<float> eventData) { }

        /// <inheritdoc />
        public void OnBeforeFocusChange(FocusEventData eventData) { }

        /// <inheritdoc />
        public void OnFocusChanged(FocusEventData eventData) { }

        /// <inheritdoc />
        public void OnPointerClicked(MixedRealityPointerEventData eventData) { }
        #endregion Unused Event Handlers

        #region Private methods

        private float GetLerpAmount()
        {
            if (smoothingActive == false || smoothingAmountOneHandManip == 0)
            {
                return 1;
            }
            // Obtained from "Frame-rate independent smoothing"
            // www.rorydriscoll.com/2016/03/07/frame-rate-independent-damping-using-lerp/
            // We divide by max value to give the slider a bit more sensitivity.
            return 1.0f - Mathf.Pow(smoothingAmountOneHandManip, Time.deltaTime);
        }

        private Dictionary<uint, Vector3> GetHandPositionMap()
        {
            var handPositionMap = new Dictionary<uint, Vector3>();
            foreach (var item in pointerIdToPointerMap)
            {
                Vector3 handPosition;
                if (item.Value.TryGetPointerPosition(out handPosition))
                {
                    handPositionMap.Add(item.Key, handPosition);
                }
            }
            return handPositionMap;
        }
        #endregion
    }
}