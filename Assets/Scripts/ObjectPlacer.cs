using MixedReality.Toolkit;
using MixedReality.Toolkit.SpatialManipulation;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ObjectPlacer : MonoBehaviour
{
    public GameObject objToPlaceRef;
    private GameObject objToPlace = null;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void Place() {
        
        objToPlace = Instantiate<GameObject>(objToPlaceRef, Vector3.zero, Quaternion.identity);
        objToPlace.transform.position = Camera.main.transform.position + (Camera.main.transform.forward * 2.0f);
        objToPlace.AddComponent<BoundsControl>();
        var collider = objToPlace.AddComponent<BoxCollider>();
        collider.center = new Vector3(0, 0.5f, 0);
        objToPlace.AddComponent<Rigidbody>();
        objToPlace.AddComponent<ConstraintManager>();
        objToPlace.AddComponent<ObjectManipulator>();
        var solverh = objToPlace.AddComponent<SolverHandler>();
        solverh.TrackedTargetType = TrackedObjectType.Head;
        var taptoplace = objToPlace.AddComponent<TapToPlace>();
        taptoplace.UseDefaultSurfaceNormalOffset = false;
        taptoplace.SurfaceNormalOffset = 0;
        taptoplace.KeepOrientationVertical = true;

        var stf = objToPlace.AddComponent<StatefulInteractable>();
        stf.selectEntered.AddListener((evt) => taptoplace.StartPlacement());
        
    }
}
