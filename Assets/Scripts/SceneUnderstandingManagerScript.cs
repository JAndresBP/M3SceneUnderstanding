using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Microsoft.MixedReality.SceneUnderstanding;
using System;
using System.Threading.Tasks;
using UnityEngine.Events;
using System.IO;
using Microsoft.MixedReality.OpenXR;
using Microsoft.Windows.Perception.Spatial;
using Microsoft.MixedReality.GraphicsTools;
using MixedReality.Toolkit;
using System.Threading;
using System.CodeDom.Compiler;

/// <summary>
/// Different rendering modes available for scene objects.
/// </summary>
public enum RenderMode
{
    Quad,
    QuadWithMask,
    Mesh,
    Wireframe
}

public class SceneUnderstandingManagerScript : MonoBehaviour
{
    [Header("Root GameObject")]
    [Tooltip("GameObject that will be the parent of all Scene Understanding related game objects. If field is left empty an empty gameobject named 'Root' will be created.")]
    public GameObject SceneRoot = null;
    [Header("Data Loader Mode")]
    [Tooltip("When enabled, the scene will be queried from a device (e.g Hololens). Otherwise, a previously saved, serialized scene will be loaded and served from your PC.")]
    public bool QuerySceneFromDevice = true;
    [Header("On Device Request Settings")]
    [Tooltip("Radius of the sphere around the camera, which is used to query the environment.")]
    [Range(5f, 100f)]
    public float BoundingSphereRadiusInMeters = 10.0f;
    [Tooltip("The scene to load when not running on the device")]
    public List<TextAsset> SUSerializedScenePaths = new List<TextAsset>(0);
    [Tooltip("When enabled, the latest data from Scene Understanding data provider will be displayed periodically (controlled by the AutoRefreshIntervalInSeconds float).")]
    public bool AutoRefresh = true;
    [Tooltip("Interval to use for auto refresh, in seconds.")]
    [Range(1f, 60f)]
    public float AutoRefreshIntervalInSeconds = 10.0f;
    [Header("Events")]
    [Tooltip("User function that get called when a Scene Understanding event happens")]
    public UnityEvent OnLoadStarted;
    [Tooltip("User function that get called when a Scene Understanding event happens")]
    public UnityEvent OnLoadFinished;
    [Header("Filters")]
    public bool FilterWorldMesh = true;
    [Header("Alignment")]
    [Tooltip("Align SU Objects Normal to Unity's Y axis")]
    public bool AlignSUObjectsNormalToUnityYAxis = true;
    [Header("Layers")]
    [Tooltip("Layer for the World mesh")]
    public int LayerForWorldObjects;
    [Header("Physics")]
    public bool AddCollidersInWorldMesh = false;
    [Tooltip("Colors for the World mesh")]
    public Color ColorForWorldObjects = new Color(0.0f, 1.0f, 1.0f, 1.0f);
    [Header("Scene Object WireFrame and Occlussion Materials")]
    [Tooltip("Material for scene object mesh wireframes.")]
    public Material SceneObjectWireframeMaterial = null;
    [Tooltip("Level Of Detail for the scene objects.")]
    public SceneMeshLevelOfDetail MeshQuality = SceneMeshLevelOfDetail.Medium;
    [Tooltip("When enabled, requests observed and inferred regions for scene objects. When disabled, requests only the observed regions for scene objects.")]
    public bool RequestInferredRegions = true;
    public GameObject menu = null;


    private float TimeElapsedSinceLastAutoRefresh = 0.0f;
    private bool DisplayFromDiskStarted = false;
    private Task displayTask = null;
    private Scene cachedDeserializedScene = null;
    private Guid LastDisplayedSceneGuid;
    private Guid LatestSceneGuid;
    private object SUDataLock = new object();
    private bool RunOnDevice;
    private byte[] LatestSUSceneData;
    private readonly int NumberOfSceneObjectsToLoadPerFrame = 5;
    private Dictionary<SceneObjectKind, Dictionary<RenderMode, Material>> materialCache;
    private readonly float MinBoundingSphereRadiusInMeters = 5f;
    private readonly float MaxBoundingSphereRadiusInMeters = 100f;

    private bool execute = true;

    // Start is called before the first frame update
    async void Start()
    {
        SceneRoot = SceneRoot == null ? new GameObject("Scene Root") : SceneRoot;

        // Considering that device is currently not supported in the editor means that
        // if the application is running in the editor it is for sure running on PC and
        // not a device. this assumption, for now, is always true.
        RunOnDevice = !Application.isEditor;
        if (QuerySceneFromDevice) {
            if (!SceneObserver.IsSupported()) {
                //QuerySceneFromDevice = false;
                Debug.LogError("Scene understanding not suported");
                return;
            }
            var access = await SceneObserver.RequestAccessAsync();
            if (access != SceneObserverAccessStatus.Allowed) {
                Debug.LogError("Access not allowed");
            }
        }
//        try
//        {
//#pragma warning disable CS4014
//            Task.Run(() => RetrieveDataContinuously());
//#pragma warning restore CS4014
//        }
//        catch (Exception e)
//        {
//            execute = false;
//            Debug.LogException(e);
//        }

    }

    // Update is called once per frame
    async void Update()
    {
        if (QuerySceneFromDevice)
        {
            if (AutoRefresh)
            {
                TimeElapsedSinceLastAutoRefresh += Time.deltaTime;
                if (TimeElapsedSinceLastAutoRefresh >= AutoRefreshIntervalInSeconds)
                {
                    try
                    {
                        await DisplayDataAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error in {nameof(SceneUnderstandingManagerScript)} {nameof(AutoRefresh)}: {ex.Message}");
                    }
                    TimeElapsedSinceLastAutoRefresh = 0.0f;
                }
            }


        }
        // If the scene is pre-loaded from disk, display it only once, as consecutive renders
        // will only bring the same result
        else if (!DisplayFromDiskStarted)
        {
            DisplayFromDiskStarted = true;
            try
            {
                await DisplayDataAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in {nameof(SceneUnderstandingManagerScript)} DisplayFromDisk: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Displays the most recently updated SU data as Unity game objects.
    /// </summary>
    /// <returns>
    /// A <see cref="Task"/> that represents the operation.
    /// </returns>
    public Task DisplayDataAsync()
    {
        // See if we already have a running task
        if ((displayTask != null) && (!displayTask.IsCompleted))
        {
            // Yes we do. Return the already running task.
            Debug.Log($"{nameof(SceneUnderstandingManagerScript)}.{nameof(DisplayDataAsync)} already in progress.");
            return displayTask;
        }
        // We have real work to do. Time to start the coroutine and track it.
        else
        {
            // Create a completion source
            TaskCompletionSource<bool> completionSource = new TaskCompletionSource<bool>();

            // Store the task
            displayTask = completionSource.Task;

            // Run Callbacks for On Load Started
            OnLoadStarted.Invoke();

            // Start the coroutine and pass in the completion source
            StartCoroutine(DisplayDataRoutine(completionSource));

            // Return the newly running task
            return displayTask;
        }
    }

    /// <summary>
    /// This coroutine will deserialize the latest SU data, either queried from the device
    /// or from disk and use it to create Unity Objects that represent that geometry
    /// </summary>
    /// <param name="completionSource">
    /// The <see cref="TaskCompletionSource{TResult}"/> that can be used to signal the coroutine is complete.
    /// </param>
    private IEnumerator DisplayDataRoutine(TaskCompletionSource<bool> completionSource)
    {
        Debug.Log("SceneUnderstandingManager.DisplayData: About to display the latest set of Scene Objects");

        //We are about to deserialize a new Scene, if we have a cached scene, dispose it.
        if (cachedDeserializedScene != null)
        {
            cachedDeserializedScene.Dispose();
            cachedDeserializedScene = null;
        }

        if (QuerySceneFromDevice)
        {
            // Get Latest Scene and Deserialize it
            // Scenes Queried from a device are Scenes composed of one Scene Fragment
            try
            {
                SceneFragment sceneFragment = GetLatestSceneSerialization();
                SceneFragment[] sceneFragmentsArray = new SceneFragment[1] { sceneFragment };
                cachedDeserializedScene = Scene.FromFragments(sceneFragmentsArray);

                // Get Latest Scene GUID
                Guid latestGuidSnapShot = GetLatestSUSceneId();
                LastDisplayedSceneGuid = latestGuidSnapShot;
            }
            catch {
                execute = false;
            }
        }
        else
        {
            // Store all the fragments and build a Scene with them
            SceneFragment[] sceneFragments = new SceneFragment[SUSerializedScenePaths.Count];
            int index = 0;
            foreach (TextAsset serializedScene in SUSerializedScenePaths)
            {
                if (serializedScene != null)
                {
                    byte[] sceneData = serializedScene.bytes;
                    SceneFragment frag = SceneFragment.Deserialize(sceneData);
                    sceneFragments[index++] = frag;
                }
            }

            try
            {
                cachedDeserializedScene = Scene.FromFragments(sceneFragments);
                lock (SUDataLock)
                {
                    // Store new GUID for data loaded
                    LatestSceneGuid = Guid.NewGuid();
                    LastDisplayedSceneGuid = LatestSceneGuid;
                }
            }
            catch (Exception inner)
            {
                // Wrap the exception
                Exception outer = new FileLoadException("Scene from PC path couldn't be loaded, verify scene fragments are not null and that they all come from the same scene.", inner);
                Debug.LogWarning(outer.Message);
                completionSource.SetException(outer);
            }
        }

        if (cachedDeserializedScene != null)
        {
            // Retrieve a transformation matrix that will allow us orient the Scene Understanding Objects into
            // their correct corresponding position in the unity world
            System.Numerics.Matrix4x4? sceneToUnityTransformAsMatrix4x4 = GetSceneToUnityTransformAsMatrix4x4(cachedDeserializedScene);

            if (sceneToUnityTransformAsMatrix4x4 != null)
            {
                // If there was previously a scene displayed in the game world, destroy it
                // to avoid overlap with the new scene about to be displayed
                DestroyAllGameObjectsUnderParent(SceneRoot.transform);

                // Allow from one frame to yield the coroutine back to the main thread
                yield return null;

                // Using the transformation matrix generated above, port its values into the tranform of the scene root (Numerics.matrix -> GameObject.Transform)
                SetUnityTransformFromMatrix4x4(SceneRoot.transform, sceneToUnityTransformAsMatrix4x4.Value, RunOnDevice);

                if (!RunOnDevice)
                {
                    // If the scene is not running on a device, orient the scene root relative to the floor of the scene
                    // and unity's up vector
                    OrientSceneForPC(SceneRoot, cachedDeserializedScene);
                }


                // After the scene has been oriented, loop through all the scene objects and
                // generate their corresponding Unity Object
                IEnumerable<SceneObject> sceneObjects = cachedDeserializedScene.SceneObjects;

                int i = 0;
                foreach (SceneObject sceneObject in sceneObjects)
                {
                    if (DisplaySceneObject(sceneObject))
                    {
                        if (++i % NumberOfSceneObjectsToLoadPerFrame == 0)
                        {
                            // Allow a certain number of objects to load before yielding back to main thread
                            yield return null;
                        }
                    }
                }
            }

            // When all objects have been loaded, finish.
            Debug.Log("SceneUnderStandingManager.DisplayData: Display Completed");

            // Run CallBacks for Onload Finished
            OnLoadFinished.Invoke();

            // Let the task complete
            completionSource.SetResult(true);
        }
    }

    private SceneFragment GetLatestSceneSerialization()
    {
        SceneFragment fragmentToReturn = null;

        lock (SUDataLock)
        {
            if (LatestSUSceneData != null)
            {
                byte[] sceneBytes = null;
                int sceneLength = LatestSUSceneData.Length;
                sceneBytes = new byte[sceneLength];

                Array.Copy(LatestSUSceneData, sceneBytes, sceneLength);

                // Deserialize the scene into a Scene Fragment
                fragmentToReturn = SceneFragment.Deserialize(sceneBytes);
            }
        }

        return fragmentToReturn;
    }

    private Guid GetLatestSUSceneId()
    {
        Guid suSceneIdToReturn;

        lock (SUDataLock)
        {
            // Return the GUID for the latest scene
            suSceneIdToReturn = LatestSceneGuid;
        }

        return suSceneIdToReturn;
    }

    /// <summary>
    /// Function to return the correspoding transformation matrix to pass geometry
    /// from the Scene Understanding Coordinate System to the Unity one
    /// </summary>
    /// <param name="scene"> Scene from which to get the Scene Understanding Coordinate System </param>
    private System.Numerics.Matrix4x4? GetSceneToUnityTransformAsMatrix4x4(Scene scene)
    {
        System.Numerics.Matrix4x4? sceneToUnityTransform = System.Numerics.Matrix4x4.Identity;
        if (RunOnDevice)
        {

            SpatialCoordinateSystem sceneCoordinateSystem = Microsoft.Windows.Perception.Spatial.Preview.SpatialGraphInteropPreview.CreateCoordinateSystemForNode(scene.OriginSpatialGraphNodeId);
            SpatialCoordinateSystem unityCoordinateSystem = PerceptionInterop.GetSceneCoordinateSystem(UnityEngine.Pose.identity) as SpatialCoordinateSystem; 


            sceneToUnityTransform = sceneCoordinateSystem.TryGetTransformTo(unityCoordinateSystem);

            if (sceneToUnityTransform != null)
            {
                sceneToUnityTransform = ConvertRightHandedMatrix4x4ToLeftHanded(sceneToUnityTransform.Value);
            }
            else
            {
                Debug.LogWarning("SceneUnderstandingManager.GetSceneToUnityTransform: Scene to Unity transform is null.");
            }
        }

        return sceneToUnityTransform;
    }

    /// <summary>
    /// Converts a right handed tranformation matrix into a left handed one
    /// </summary>
    /// <param name="matrix"> Matrix to convert </param>
    private System.Numerics.Matrix4x4 ConvertRightHandedMatrix4x4ToLeftHanded(System.Numerics.Matrix4x4 matrix)
    {
        matrix.M13 = -matrix.M13;
        matrix.M23 = -matrix.M23;
        matrix.M43 = -matrix.M43;

        matrix.M31 = -matrix.M31;
        matrix.M32 = -matrix.M32;
        matrix.M34 = -matrix.M34;

        return matrix;
    }

    /// <summary>
    /// Function to destroy all children under a Unity Transform
    /// </summary>
    /// <param name="parentTransform"> Parent Transform to remove children from </param>
    private void DestroyAllGameObjectsUnderParent(Transform parentTransform)
    {
        if (parentTransform == null)
        {
            Debug.LogWarning("SceneUnderstandingManager.DestroyAllGameObjectsUnderParent: Parent is null.");
            return;
        }

        foreach (Transform child in parentTransform)
        {
            Destroy(child.gameObject);
        }
    }

    /// <summary>
    /// Passes all the values from a 4x4 tranformation matrix into a Unity Tranform
    /// </summary>
    /// <param name="targetTransform"> Transform to pass the values into                                    </param>
    /// <param name="matrix"> Matrix from which the values to pass are gathered                             </param>
    /// <param name="updateLocalTransformOnly"> Flag to update local transform or global transform in unity </param>
    private void SetUnityTransformFromMatrix4x4(Transform targetTransform, System.Numerics.Matrix4x4 matrix, bool updateLocalTransformOnly = false)
    {
        if (targetTransform == null)
        {
            Debug.LogWarning("SceneUnderstandingManager.SetUnityTransformFromMatrix4x4: Unity transform is null.");
            return;
        }

        Vector3 unityTranslation;
        Quaternion unityQuat;
        Vector3 unityScale;

        System.Numerics.Vector3 vector3;
        System.Numerics.Quaternion quaternion;
        System.Numerics.Vector3 scale;

        System.Numerics.Matrix4x4.Decompose(matrix, out scale, out quaternion, out vector3);

        unityTranslation = new Vector3(vector3.X, vector3.Y, vector3.Z);
        unityQuat = new Quaternion(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        unityScale = new Vector3(scale.X, scale.Y, scale.Z);

        if (updateLocalTransformOnly)
        {
            targetTransform.localPosition = unityTranslation;
            targetTransform.localRotation = unityQuat;
        }
        else
        {
            targetTransform.SetPositionAndRotation(unityTranslation, unityQuat);
        }
    }

    /// <summary>
    /// Orients a GameObject relative to Unity's Up vector and Scene Understanding's Largest floor's normal vector
    /// </summary>
    /// <param name="sceneRoot"> Unity object to orient                       </param>
    /// <param name="suScene"> SU object to obtain the largest floor's normal </param>
    private void OrientSceneForPC(GameObject sceneRoot, Scene suScene)
    {
        if (suScene == null)
        {
            Debug.Log("SceneUnderstandingManager.OrientSceneForPC: Scene Understanding Scene Data is null.");
        }

        IEnumerable<SceneObject> sceneObjects = suScene.SceneObjects;

        float largestFloorAreaFound = 0.0f;
        SceneObject suLargestFloorObj = null;
        SceneQuad suLargestFloorQuad = null;
        foreach (SceneObject sceneObject in sceneObjects)
        {
            if (sceneObject.Kind == SceneObjectKind.Floor)
            {
                IEnumerable<SceneQuad> quads = sceneObject.Quads;

                if (quads != null)
                {
                    foreach (SceneQuad quad in quads)
                    {
                        float quadArea = quad.Extents.X * quad.Extents.Y;

                        if (quadArea > largestFloorAreaFound)
                        {
                            largestFloorAreaFound = quadArea;
                            suLargestFloorObj = sceneObject;
                            suLargestFloorQuad = quad;
                        }
                    }
                }
            }
        }

        if (suLargestFloorQuad != null)
        {
            float quadWith = suLargestFloorQuad.Extents.X;
            float quadHeight = suLargestFloorQuad.Extents.Y;

            System.Numerics.Vector3 p1 = new System.Numerics.Vector3(-quadWith / 2, -quadHeight / 2, 0);
            System.Numerics.Vector3 p2 = new System.Numerics.Vector3(quadWith / 2, -quadHeight / 2, 0);
            System.Numerics.Vector3 p3 = new System.Numerics.Vector3(-quadWith / 2, quadHeight / 2, 0);

            System.Numerics.Matrix4x4 floorTransform = suLargestFloorObj.GetLocationAsMatrix();
            floorTransform = ConvertRightHandedMatrix4x4ToLeftHanded(floorTransform);

            System.Numerics.Vector3 tp1 = System.Numerics.Vector3.Transform(p1, floorTransform);
            System.Numerics.Vector3 tp2 = System.Numerics.Vector3.Transform(p2, floorTransform);
            System.Numerics.Vector3 tp3 = System.Numerics.Vector3.Transform(p3, floorTransform);

            System.Numerics.Vector3 p21 = tp2 - tp1;
            System.Numerics.Vector3 p31 = tp3 - tp1;

            System.Numerics.Vector3 floorNormal = System.Numerics.Vector3.Cross(p31, p21);

            Vector3 floorNormalUnity = new Vector3(floorNormal.X, floorNormal.Y, floorNormal.Z);

            Quaternion rotation = Quaternion.FromToRotation(floorNormalUnity, Vector3.up);
            SceneRoot.transform.rotation = rotation;
        }
    }

    /// <summary>
    /// Create a Unity Game Object for an individual Scene Understanding Object
    /// </summary>
    /// <param name="suObject">The Scene Understanding Object to generate in Unity</param>
    private bool DisplaySceneObject(SceneObject suObject)
    {
        if (suObject == null)
        {
            Debug.LogWarning("SceneUnderstandingManager.DisplaySceneObj: Object is null");
            return false;
        }


        // If an individual type of object is requested to not be rendered, avoid generation of unity object
        SceneObjectKind kind = suObject.Kind;
        switch (kind)
        {
            case SceneObjectKind.World:
                if (FilterWorldMesh)
                    return false;
                break;
        }

        // This gameobject will hold all the geometry that represents the Scene Understanding Object
        GameObject unityParentHolderObject = new GameObject(suObject.Kind.ToString());
        unityParentHolderObject.transform.parent = SceneRoot.transform;

        // Scene Understanding uses a Right Handed Coordinate System and Unity uses a left handed one, convert.
        System.Numerics.Matrix4x4 converted4x4LocationMatrix = ConvertRightHandedMatrix4x4ToLeftHanded(suObject.GetLocationAsMatrix());
        // From the converted Matrix pass its values into the unity transform (Numerics -> Unity.Transform)
        SetUnityTransformFromMatrix4x4(unityParentHolderObject.transform, converted4x4LocationMatrix, true);

        // This list will keep track of all the individual objects that represent the geometry of
        // the Scene Understanding Object
        List<GameObject> unityGeometryObjects = null;
        switch (kind)
        {
            // Create all the geometry and store it in the list
            case SceneObjectKind.World:
                unityGeometryObjects = CreateWorldMeshInUnity(suObject);
                break;
            default:
                unityGeometryObjects = CreateSUObjectInUnity(suObject);
                break;
        }

        // For all the Unity Game Objects that represent The Scene Understanding Object
        // Of this iteration, make sure they are all children of the UnityParent object
        // And that their local postion and rotation is relative to their parent
        foreach (GameObject geometryObject in unityGeometryObjects)
        {
            geometryObject.transform.parent = unityParentHolderObject.transform;
            geometryObject.transform.localPosition = Vector3.zero;

            if (AlignSUObjectsNormalToUnityYAxis)
            {
                // If our Vertex Data is rotated to have it match its Normal to Unity's Y axis, we need to offset the rotation
                // in the parent object to have the object face the right direction
                geometryObject.transform.localRotation = Quaternion.Euler(-90.0f, 0.0f, 0.0f);
            }
            else
            {
                //Otherwise don't rotate
                geometryObject.transform.localRotation = Quaternion.identity;
            }
        }

        // Add a SceneUnderstandingProperties Component to the Parent Holder Object
        // this component will hold a GUID and a SceneObjectKind that correspond to this
        // specific Object 
        SceneUnderstandingProperties properties = unityParentHolderObject.AddComponent<SceneUnderstandingProperties>();
        properties.suObjectGUID = suObject.Id;
        properties.suObjectKind = suObject.Kind;

        //Return that the Scene Object was indeed represented as a unity object and wasn't skipped
        return true;
    }

    /// <summary>
    /// Create a world Mesh Unity Object that represents the World Mesh Scene Understanding Object
    /// </summary>
    /// <param name="suObject">The Scene Understanding Object to generate in Unity</param>
    private List<GameObject> CreateWorldMeshInUnity(SceneObject suObject)
    {
        // The World Mesh Object is different from the rest of the Scene Understanding Objects
        // in the Sense that its unity representation is not affected by the filters or Request Modes
        // in this component, the World Mesh Renders even of the Scene Objects are disabled and
        // the World Mesh is always represented with a WireFrame Material, different to the Scene
        // Understanding Objects whose materials vary depending on the Settings in the component

        IEnumerable<SceneMesh> suMeshes = suObject.Meshes;
        Mesh unityMesh = GenerateUnityMeshFromSceneObjectMeshes(suMeshes);

        GameObject gameObjToReturn = new GameObject(suObject.Kind.ToString());
        gameObjToReturn.layer = LayerForWorldObjects;
        Material tempMaterial = GetMaterial(SceneObjectKind.World, RenderMode.Wireframe);
        AddMeshToUnityObject(gameObjToReturn, unityMesh, ColorForWorldObjects, tempMaterial);

        if (AddCollidersInWorldMesh)
        {
            // Generate a unity mesh for physics
            Mesh unityColliderMesh = GenerateUnityMeshFromSceneObjectMeshes(suObject.ColliderMeshes);

            MeshCollider col = gameObjToReturn.AddComponent<MeshCollider>();
            col.sharedMesh = unityColliderMesh;
        }

        // Also the World Mesh is represented as one big Mesh in Unity, different to the rest of SceneObjects
        // Where their multiple meshes are represented in separate game objects
        return new List<GameObject> { gameObjToReturn };
    }

    /// <summary>
    /// Create a unity Mesh from a set of Scene Understanding Meshes
    /// </summary>
    /// <param name="suMeshes">The Scene Understanding mesh to generate in Unity</param>
    private Mesh GenerateUnityMeshFromSceneObjectMeshes(IEnumerable<SceneMesh> suMeshes)
    {
        if (suMeshes == null)
        {
            Debug.LogWarning("SceneUnderstandingManager.GenerateUnityMeshFromSceneObjectMeshes: Meshes is null.");
            return null;
        }

        // Retrieve the data and store it as Indices and Vertices
        List<int> combinedMeshIndices = new List<int>();
        List<Vector3> combinedMeshVertices = new List<Vector3>();

        foreach (SceneMesh suMesh in suMeshes)
        {
            if (suMesh == null)
            {
                Debug.LogWarning("SceneUnderstandingManager.GenerateUnityMeshFromSceneObjectMeshes: Mesh is null.");
                continue;
            }

            uint[] meshIndices = new uint[suMesh.TriangleIndexCount];
            suMesh.GetTriangleIndices(meshIndices);

            System.Numerics.Vector3[] meshVertices = new System.Numerics.Vector3[suMesh.VertexCount];
            suMesh.GetVertexPositions(meshVertices);

            uint indexOffset = (uint)combinedMeshVertices.Count;

            // Store the Indices and Vertices
            for (int i = 0; i < meshVertices.Length; i++)
            {
                // Here Z is negated because Unity Uses Left handed Coordinate system and Scene Understanding uses Right Handed
                combinedMeshVertices.Add(new Vector3(meshVertices[i].X, meshVertices[i].Y, -meshVertices[i].Z));
            }

            for (int i = 0; i < meshIndices.Length; i++)
            {
                combinedMeshIndices.Add((int)(meshIndices[i] + indexOffset));
            }
        }

        Mesh unityMesh = new Mesh();

        // Unity has a limit of 65,535 vertices in a mesh.
        // This limit exists because by default Unity uses 16-bit index buffers.
        // Starting with 2018.1, Unity allows one to use 32-bit index buffers.
        if (combinedMeshVertices.Count > 65535)
        {
            Debug.Log("SceneUnderstandingManager.GenerateUnityMeshForSceneObjectMeshes: CombinedMeshVertices count is " + combinedMeshVertices.Count + ". Will be using a 32-bit index buffer.");
            unityMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }

        if (AlignSUObjectsNormalToUnityYAxis)
        {
            //Rotate our Vertex Data to match our Object's Normal vector to Unity's coordinate system Up Axis (Y axis)
            Quaternion rot = Quaternion.Euler(90.0f, 0.0f, 0.0f);
            for (int i = 0; i < combinedMeshVertices.Count; i++)
            {
                combinedMeshVertices[i] = rot * combinedMeshVertices[i];
            }
        }

        // Apply the Indices and Vertices
        unityMesh.SetVertices(combinedMeshVertices);
        unityMesh.SetIndices(combinedMeshIndices.ToArray(), MeshTopology.Triangles, 0);
        unityMesh.RecalculateNormals();

        return unityMesh;
    }

    /// <summary>
    /// Get the cached material for each SceneObject Kind
    /// </summary>
    /// <param name="kind">
    /// The <see cref="SceneObjectKind"/> to obtain the material for.
    /// </param>
    /// <param name="mode">
    /// The <see cref="RenderMode"/> to obtain the material for.
    /// </param>
    /// <remarks>
    /// If <see cref="IsInGhostMode"/> is true, the ghost material will be returned.
    /// </remarks>
    private Material GetMaterial(SceneObjectKind kind, RenderMode mode)
    {
       
        // Make sure we have a cache
        if (materialCache == null) { materialCache = new Dictionary<SceneObjectKind, Dictionary<RenderMode, Material>>(); }

        // Find or create cache specific to this Kind
        Dictionary<RenderMode, Material> kindModeCache;
        if (!materialCache.TryGetValue(kind, out kindModeCache))
        {
            kindModeCache = new Dictionary<RenderMode, Material>();
            materialCache[kind] = kindModeCache;
        }

        // Find or create material specific to this Mode
        Material mat;
        if (!kindModeCache.TryGetValue(mode, out mat))
        {
            // Determine the source material by kind
            Material sourceMat = SceneObjectWireframeMaterial;
            //switch (mode)
            //{
            //    case RenderMode.Quad:
            //    case RenderMode.QuadWithMask:
            //        sourceMat = GetSceneObjectSourceMaterial(RenderMode.Quad, kind);
            //        break;
            //    case RenderMode.Wireframe:
            //        sourceMat = SceneObjectWireframeMaterial;
            //        break;
            //    default:
            //        sourceMat = GetSceneObjectSourceMaterial(RenderMode.Mesh, kind);
            //        break;
            //}

            // Create an instance
            mat = Instantiate(sourceMat);

            // Set color to match the kind
            //Color? color = GetColor(kind);
            Color? color = ColorForWorldObjects;
            if (color != null)
            {
                mat.color = color.Value;
                mat.SetColor("_WireColor", color.Value);
            }

            // Store
            kindModeCache[mode] = mat;
        }

        // Return the found or created material
        return mat;
    }

    /// <summary>
    /// Function to add a Mesh to a Unity Object
    /// </summary>
    /// <param name="unityObject">The unity object to where the mesh will be applied </param>
    /// <param name="mesh"> Mesh to be applied                                       </param>
    /// <param name="color"> Color to apply to the Mesh                              </param>
    /// <param name="material"> Material to apply to the unity Mesh Renderer         </param>
    private void AddMeshToUnityObject(GameObject unityObject, Mesh mesh, Color? color, Material material)
    {
        if (unityObject == null || mesh == null || material == null)
        {
            Debug.Log("SceneUnderstandingManager.AddMeshToUnityObject: One or more arguments are null");
        }

        MeshFilter mf = unityObject.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        MeshRenderer mr = unityObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
    }

    /// <summary>
    /// Create a list of Unity GameObjects that represent all the Meshes/Geometry in a Scene
    /// Understanding Object
    /// </summary>
    /// <param name="suObject">The Scene Understanding Object to generate in Unity</param>
    private List<GameObject> CreateSUObjectInUnity(SceneObject suObject)
    {
        // Each SU object has a specific type, query for its correspoding color
        // according to its type
        Color? color = ColorForWorldObjects; //GetColor(suObject.Kind);
        int layer = LayerForWorldObjects;//GetLayer(suObject.Kind);

        List<GameObject> listOfGeometryGameObjToReturn = new List<GameObject>();
       
        // Then Create the Planar Meshes Scene Objects
        {
            // If the Request Settings are requesting Meshes or WireFrame, create a gameobject in unity for
            // each Mesh, and apply either the default material or the wireframe material
            for (int i = 0; i < suObject.Meshes.Count; i++)
            {
                SceneMesh suGeometryMesh = suObject.Meshes[i];
                SceneMesh suColliderMesh = suObject.ColliderMeshes[i];

                // Generate the unity mesh for the Scene Understanding mesh.
                Mesh unityMesh = GenerateUnityMeshFromSceneObjectMeshes(new List<SceneMesh> { suGeometryMesh });
                GameObject gameObjectToReturn = new GameObject(suObject.Kind.ToString() + "Mesh");
                gameObjectToReturn.layer = layer;

                Material tempMaterial = SceneObjectWireframeMaterial;

                // Add the created Mesh into the Unity Object
                AddMeshToUnityObject(gameObjectToReturn, unityMesh, color, tempMaterial);

                // Generate a unity mesh for physics
                Mesh unityColliderMesh = GenerateUnityMeshFromSceneObjectMeshes(new List<SceneMesh> { suColliderMesh });
                MeshCollider col = gameObjectToReturn.AddComponent<MeshCollider>();
                col.sharedMesh = unityColliderMesh;

                var statefullInteractable = gameObjectToReturn.AddComponent<StatefulInteractable>();
                statefullInteractable.selectEntered.AddListener((evt) => menu.SetActive(true));

                // Add to list
                listOfGeometryGameObjToReturn.Add(gameObjectToReturn);
            }
        }

        // Return all the Geometry GameObjects that represent a Scene
        // Understanding Object
        return listOfGeometryGameObjToReturn;
    }

    ///// <summary>
    ///// Retrieves Scene Understanding data continuously from the runtime.
    ///// </summary>
    private void RetrieveDataContinuously()
    {
        // At the beginning, retrieve only the observed scene object meshes.
        RetrieveData(BoundingSphereRadiusInMeters, false, true, false, false, SceneMeshLevelOfDetail.Coarse);

        while (execute)
        {
            // Always request quads, meshes and the world mesh. SceneUnderstandingManager will take care of rendering only what the user has asked for.
            RetrieveData(BoundingSphereRadiusInMeters, true, true, RequestInferredRegions, true, MeshQuality);
        }
    }

    /// <summary>
    /// Calls into the Scene Understanding APIs, to retrieve the latest scene as a byte array.
    /// </summary>
    /// <param name="enableQuads">When enabled, quad representation of scene objects is retrieved.</param>
    /// <param name="enableMeshes">When enabled, mesh representation of scene objects is retrieved.</param>
    /// <param name="enableInference">When enabled, both observed and inferred scene objects are retrieved. Otherwise, only observed scene objects are retrieved.</param>
    /// <param name="enableWorldMesh">When enabled, retrieves the world mesh.</param>
    /// <param name="lod">If world mesh is enabled, lod controls the resolution of the mesh returned.</param>
    private void RetrieveData(float boundingSphereRadiusInMeters, bool enableQuads, bool enableMeshes, bool enableInference, bool enableWorldMesh, SceneMeshLevelOfDetail lod)
    {
        //Debug.Log("SceneUnderstandingManager.RetrieveData: Started.");

        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();

        try
        {
            SceneQuerySettings querySettings;
            querySettings.EnableSceneObjectQuads = enableQuads;
            querySettings.EnableSceneObjectMeshes = enableMeshes;
            querySettings.EnableOnlyObservedSceneObjects = !enableInference;
            querySettings.EnableWorldMesh = enableWorldMesh;
            querySettings.RequestedMeshLevelOfDetail = lod;

            // Ensure that the bounding radius is within the min/max range.
            boundingSphereRadiusInMeters = Mathf.Clamp(boundingSphereRadiusInMeters, MinBoundingSphereRadiusInMeters, MaxBoundingSphereRadiusInMeters);

            // Make sure the scene query has completed swap with latestSUSceneData under lock to ensure the application is always pointing to a valid scene.
            SceneBuffer serializedScene = SceneObserver.ComputeSerializedAsync(querySettings, boundingSphereRadiusInMeters).GetAwaiter().GetResult();
            lock (SUDataLock)
            {
                // The latest data queried from the device is stored in these variables
                LatestSUSceneData = new byte[serializedScene.Size];
                serializedScene.GetData(LatestSUSceneData);
                LatestSceneGuid = Guid.NewGuid();
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        stopwatch.Stop();
        /*
        Debug.Log(string.Format("SceneUnderstandingManager.RetrieveData: Completed. Radius: {0}; Quads: {1}; Meshes: {2}; Inference: {3}; WorldMesh: {4}; LOD: {5}; Bytes: {6}; Time (secs): {7};",
                                boundingSphereRadiusInMeters,
                                enableQuads,
                                enableMeshes,
                                enableInference,
                                enableWorldMesh,
                                lod,
                                (LatestSUSceneData == null ? 0 : LatestSUSceneData.Length),
                                stopwatch.Elapsed.TotalSeconds));
        */
    }
}
