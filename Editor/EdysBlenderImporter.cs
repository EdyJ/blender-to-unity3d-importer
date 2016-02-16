/*
Blender to Unity 3D importer
============================

An advanced Blender model importer for Unity 3D
https://github.com/EdyJ/blender-to-unity3d-importer

By Angel García "Edy"
http://www.edy.es
*/


using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections;
using System.Collections.Generic;


public class EdysBlenderImporter : AssetPostprocessor
	{
	private bool m_fixBlender = true;
	private bool m_optimize = false;
	private bool m_zReverse = false;
	private bool m_animFix = true;
	private bool m_floatFix = true;
	private bool m_postMods = true;
	private bool m_forceFixRoot = false;

	private float m_floatFixThreshold = 1.53e-05f;


	void OnPostprocessModel (GameObject go)
		{
		string filePath = assetPath.ToLowerInvariant();
		if (Path.GetExtension(filePath) != ".blend") m_fixBlender = false;

		// Look for specific importer commands in the file name or path.
		// Imported objects can be located in a subfolder named with the import commands.
		// If both path and file are used then the last one has preference.

		int i1 = filePath.LastIndexOf('[');
		int i2 = filePath.LastIndexOf(']');
		if (i1 < 0 || i2 < 0 || i1 >= i2) return;

		string[] tokens = filePath.Substring(i1+1, i2-i1-1).Split('.');
		if (tokens.Length == 0 || tokens[0] != "importer") return;

		string options = "";

		for (int i=1, c=tokens.Length; i<c; i++)
			{
			string token = tokens[i];

			switch (token)
				{
				case "skipfix": m_fixBlender = false; break;
				case "forcefix": m_fixBlender = true; break;
				case "opt": m_optimize = true; break;
				case "zreverse": m_zReverse = true; break;
				case "noanimfix": m_animFix = false; break;
				case "nofloatfix": m_floatFix = false; break;
				case "nomods": m_postMods = false; break;
				case "forcefixroot": m_forceFixRoot = true; break;

				default:
					token = "";
					break;
				}

			if (token != "") options += token + " ";
			}

		// Process the file with the specified options

		if (m_fixBlender || m_optimize)
			{
			// Clean object's name (just for debug purposes, no effect on the imported object's name)

			string name = go.name.ToLowerInvariant();
			i1 = name.IndexOf("[importer");
			i2 = name.IndexOf("]");
			if (i1 >= 0 && i2 >= 0 && i1 < i2)
				go.name = name.Remove(i1, i2-i1+1).Trim(new []{' ','_'});

			// Go processing

			LogClear();
			LogInfo("EDY's ADVANCED MODEL IMPORTER:  " + go.name + "   Options: " + options);
			LogInfo("Click for details");
			LogInfo("");

			if (m_fixBlender)
				{
				ProcessBlenderObject(go);
				LogInfo("Blender file imported successfully.");
				}

			if (m_optimize)
				{
				MeshFilter[] meshes = go.GetComponentsInChildren<MeshFilter>(true);

				LogInfo(string.Format("{0} meshes loaded. Searching for duplicates...", meshes.Length));
				OptimizeMeshes(meshes);
				}

			LogFlush();
			}
		}


	// Menu option for finding duplicated meshes in the current scene and reference them
	// as instances of a single unique mesh.
	//
	// Only the meshes that are referenced in some way are included in the build,
	// even if the original 3D file contains many more meshes.

	[MenuItem ("GameObject/Optimize mesh instances in this scene")]
	static void OptimizeMeshInstancesMenu ()
		{
		LogClear();

		MeshFilter[] meshes = GameObject.FindObjectsOfType<MeshFilter>();

		LogInfo(string.Format("{0} meshes found in the scene. Searching for duplicates...", meshes.Length));
		LogInfo("Click for details");

		OptimizeMeshes(meshes);
		LogFlush();
		}


	static void OptimizeMeshes (MeshFilter[] meshes)
		{
		int unique;
		int instanced;

		OptimizeMeshInstances(meshes, out unique, out instanced);

		if (instanced > 0)
			LogInfo(string.Format("{0} duplicated meshes found and instanced. Total {1} unique meshes.", instanced, unique));
		else
			LogInfo("No instances found. All meshes are unique.");
		}


	// Gameobject processing -----------------------------------------------------------------------


	void ProcessBlenderObject (GameObject go)
		{
		// A) If there's only one first-level object then it gets imported as a single object
		// containing the mesh (MeshFilter). This object is rotated X-90.
		//
		// B) If the Blender file contains multiple first-level objects then they are imported
		// as a single empty parent (no mesh) with all objects as children. The parent is NOT
		// rotated in this case, but all the first-level children are.

		if (m_forceFixRoot || go.GetComponent<MeshFilter>() != null || IsMultipartObject(go))
			{
			// Case A: Root object has been rotated X-90

			LogInfo("Fixing X-90 rotation in root object...");

			ProcessFirstLevel(go);
			ProcessChildren(go);
			}
		else
			{
			// Case B: First-level objects have been rotated X-90, not the root

			LogInfo(string.Format("Fixing X-90 rotation in {0} objects...", go.transform.childCount));

			foreach (Transform child in go.transform)
				{
				ProcessFirstLevel(child.gameObject);
				ProcessChildren(child.gameObject);
				}
			}

		// Fix animations.
		// Objects that have been fixed for X-90 are always imported as first-level objects
		// in the animation clips.

		if (m_animFix)
			ProcessAnimations(go);
		}


	void ProcessFirstLevel (GameObject go)
		{
		// Fix the X-90 rotation in the first level objects

		go.transform.Rotate(Vector3.right, 90.0f);
		RotateMesh(go, Quaternion.AngleAxis(-90.0f, Vector3.right));

		// Turn the model around so the +Z axis = forward (in Blender +Y = forward)

		if (m_zReverse)
			{
			Quaternion q = Quaternion.AngleAxis(180.0f, Vector3.up);
			go.transform.localPosition = q * go.transform.localPosition;
			RotateMesh(go, q);
			RotationFix180(go.transform);
			}

		// Convert scale

		ConvertScale(go.transform);

		// Apply per-object commands and fix the dirty float values (floating-point precision)

		if (m_postMods) ApplyPostModifiers(go);
		if (m_floatFix) FixFloatValues(go.transform, m_floatFixThreshold);
		}


	void ProcessChildren (GameObject go)
		{
		// 2nd level and below object: adjust local position and rotate the object

		Quaternion q = Quaternion.AngleAxis(-90.0f, Vector3.right);
		if (m_zReverse) q = Quaternion.AngleAxis(180.0f, Vector3.up) * q;

		foreach (Transform child in go.transform)
			{
			child.localPosition = q * child.localPosition;
			RotateMesh(child.gameObject, q);

			// Convert rotation and scale from Blender (Right-handed) to Unity (Left-handed)

			ConvertRotation(child);
			if (m_zReverse) RotationFix180(child);
			ConvertScale(child);

			// Postprocess

			if (m_postMods) ApplyPostModifiers(child.gameObject);
			if (m_floatFix) FixFloatValues(child, m_floatFixThreshold);

			// Recursively fix children objects

			ProcessChildren(child.gameObject);
			}
		}


	void ProcessAnimations (GameObject go)
		{
		AnimationClip[] clips = AnimationUtility.GetAnimationClips(go);

		if (clips.Length > 0)
			{
			LogInfo(clips.Length + " animation clips found");

			foreach (AnimationClip clip in clips)
				{
				// Retrieve a list of unique objects animated by this clip

				List<string> animatedObjects = new List<string>();
				EditorCurveBinding[] curveBindings = AnimationUtility.GetCurveBindings(clip);

				foreach (EditorCurveBinding binding in curveBindings)
					{
					if (!animatedObjects.Contains(binding.path))
						animatedObjects.Add(binding.path);
					}

				LogInfo("Animation clip \"" + clip.name + "\" references " + animatedObjects.Count + " objects");

				// Fix the animation curves on each object

				foreach (string objectName in animatedObjects)
					{
					ProcessClipRotation(clip, objectName);
					ProcessClipPosition(clip, objectName);
					ProcessClipScale(clip, objectName);
					}
				}
			}
		}


	// Object manipulation functions ---------------------------------------------------------------


	bool IsMultipartObject (GameObject go)
		{
		// An object has been subdivided by Unity when it doesn't contain a mesh
		// and the names of all its childer end with "_MeshPart<n>".
		// Example: _MeshPart0, _MeshPart1, _MeshPart2...

		foreach (Transform child in go.transform)
			{
			if (!child.gameObject.name.TrimEnd("0123456789".ToCharArray()).EndsWith("_MeshPart"))
				return false;
			}

		return true;
		}


	void ApplyPostModifiers (GameObject go)
		{
		string name = go.name.ToLowerInvariant();
		string options = "";

		int idx;
		while ((idx = name.LastIndexOf("--")) != -1)
			{
			string token = name.Substring(idx+2);
			name = name.Remove(idx);

			switch (token)
				{
					// Remove Mesh Renderer component

				case "norend":
					Object.DestroyImmediate(go.GetComponent<Renderer>());
					break;

					// Add a mesh collider

				case "coll":
					go.AddComponent<MeshCollider>();
					break;

				case "collconv":
					MeshCollider col = go.AddComponent<MeshCollider>();
					col.convex = true;
					break;

				default:
					token = "";
					break;
				}

			if (token != "") options += (options == ""? "" : " ") + token;
			}

		// Clean the object's name. The mesh name in the Assets will preserve the original string,
		// but in Hierarchy the name will not show the commands.

		// BUG-TODO: We used to rename the object for removing the commands. But this breaks
		// the binding with the animations for this object. They appear in red in Unity's
		// Animation inspector refering the original name. We must research if there's a way for
		// renaming the object in the animation bindings. If so, we would create a list of
		// of "animation object renames" that would be applied when processing animations.
		// So atm the name is trimmed for the log line only.

		// go.name = name.TrimEnd(new []{'_'});
		string goName = name.TrimEnd(new []{'_'});

		if (options != "")
			LogInfo(string.Format("Mesh commands: {0}  [{1}]", goName, options));
		}


	static void RotateMesh (GameObject go, Quaternion rot)
		{
		Mesh mesh = null;
		MeshFilter meshFilter = go.GetComponent<MeshFilter>();
		if (meshFilter) mesh = meshFilter.sharedMesh;

		if (mesh)
			{
			Vector3[] vertices = mesh.vertices;
			Vector3[] normals = mesh.normals;

			for (int i=0, c=vertices.Length; i<c; i++)
				vertices[i] = rot * vertices[i];

			for (int i=0, c=normals.Length; i<c; i++)
				normals[i] = (rot * normals[i]).normalized;

			mesh.vertices = vertices;
			mesh.normals = normals;
			mesh.RecalculateBounds();

			if (mesh.tangents.Length > 0)
				CalculateMeshTangents(mesh);
			}
		}


	static void ConvertRotation (Transform t)
		{
		Quaternion q = t.localRotation;
		t.localRotation = new Quaternion(-q.x, -q.z, q.y, -q.w);
		}


	static void RotationFix180 (Transform t)
		{
		Quaternion q = t.localRotation;
		t.localRotation = new Quaternion(-q.x, q.y, -q.z, q.w);
		}


	static void ConvertScale (Transform t)
		{
		Vector3 localScale = t.localScale;

		float scaleY = localScale.y;
		localScale.y = localScale.z;
		localScale.z = scaleY;

		t.localScale = localScale;
		}


	static void FixFloatValues (Transform t, float threshold)
		{
		t.localPosition = FixVector3(t.localPosition, threshold);
		t.localEulerAngles = FixVector3(t.localEulerAngles, threshold);
		t.localScale = FixVector3(t.localScale, threshold);
		}


	static Vector3 FixVector3 (Vector3 v, float threshold)
		{
		return new Vector3(FixFloat(v.x, threshold), FixFloat(v.y, threshold), FixFloat(v.z, threshold));
		}


	static float FixFloat (float value, float threshold)
		{
		float nearest = Mathf.Round (value);
		return Mathf.Abs(value - nearest) <= threshold? nearest : value;
		}


	// Mesh instance optimization ------------------------------------------------------------------


	static void OptimizeMeshInstances (MeshFilter[] meshFilters, out int unique, out int instanced)
		{
		Dictionary<uint, Mesh> meshes = new Dictionary<uint, Mesh>();

		instanced = 0;
		unique = 0;

		foreach (MeshFilter mf in meshFilters)
			{
			// The MeshFilter might have no mesh assigned

			if (mf.sharedMesh == null) continue;

			// Calculate its hash

			uint key = CalculateMeshHash(mf.sharedMesh);

			// Check if the hash exists in the dictionary
			//  - Exists: assign the existing reference to the mesh
			//	- Doesn't exists: add the reference to the dictionary

			if (meshes.ContainsKey(key))
				{
				Mesh existingMesh = meshes[key];

				if (mf.sharedMesh != existingMesh)
					{
					Object.DestroyImmediate(mf.sharedMesh);
					mf.sharedMesh = meshes[key];
					instanced++;
					// LogInfo(string.Format("Mesh: {0} is identical to: {1} (#{2})  Instance found!", mf.name, meshes[key].name, key.ToString("X8")));
					}
				else
					{
					// LogInfo(string.Format("Mesh: {0} already instanced: {1} (#{2})", mf.name, meshes[key].name, key));
					}
				}
			else
				{
				meshes.Add(key, mf.sharedMesh);
				}
			}

		unique = meshes.Count;
		}



	static uint CalculateMeshHash (Mesh mesh)
		{
		// Calculate an unique (I hope) hash value per mesh.
		// If anyone has a better idea on how to do this, please let me know.

		uint hash = 0;
		uint count = 0;
		Vector3 scale = new Vector3(2039.0f, 2053.0f, 2063.0f);

		Bounds bounds = mesh.bounds;
		Vector3 extent = bounds.max - bounds.min;
		Vector3 invExtent = new Vector3(1.0f / extent.x, 1.0f / extent.y, 1.0f / extent.z);

		// Vertex hash
		// Each vertex is scaled down to 0..1 inside the bounding volume. Then an arbitrary
		// limited scale is applied and the three components are added together.

		foreach (Vector3 v in mesh.vertices)
			{
			Vector3 vertexBound = Vector3.Scale(v-bounds.min, invExtent);
			uint vertexHash = (uint)Vector3.Dot(vertexBound, scale);

			hash += vertexHash * count++;
			}

		// Consider the position of the bounds themselves (two identical meshes with different
		// origins must have different hashes)

		uint boundHash = (uint)(Vector3.Dot(bounds.max, new Vector3(1.0f,3.0f,5.0f)) + Vector3.Dot(bounds.min, new Vector3(3.0f,5.0f,1.0f)));
		hash += boundHash * count;

		count = 7;

		// Indexes hash
		// Must be calculated per submesh for considering different materials.
		// I haven't seen subMeshCount = 0, but who knows...

		if (mesh.subMeshCount == 0)
			{
			foreach (int t in mesh.triangles)
				hash += (uint)t * count;
			}
		else
			{
			for (int s=0, c=mesh.subMeshCount; s<c; s++)
				{
				int[] triangles = mesh.GetTriangles(s);

				foreach (int t in triangles)
					hash += (uint)t * count;

				count++;
				}
			}

		hash += count * (uint)mesh.subMeshCount;

		count = 11;

		// Normals hash

		Vector3 boundNormal = Vector3.one * 0.5f;

		foreach (Vector3 v in mesh.normals)
			{
			Vector3 vertexBound = Vector3.Scale(v+Vector3.one, boundNormal);
			uint vertexHash = (uint)Vector3.Dot(vertexBound, scale);

			hash += vertexHash * count++;
			}

		count = 13;

		// Tangent hash (not required for Blender)

		foreach (Vector3 v in mesh.tangents)
			{
			Vector3 vertexBound = Vector3.Scale(v+Vector3.one, boundNormal);
			uint vertexHash = (uint)Vector3.Dot(vertexBound, scale);

			hash += vertexHash * count++;
			}

		count = 17;

		// UV1 hash

		foreach (Vector2 v in mesh.uv)
			{
			uint uvHash = (uint)Vector2.Dot(v, scale);
			hash += uvHash * count++;
			}

		count = 19;

		// UV2 hash

		foreach (Vector2 v in mesh.uv2)
			{
			uint uvHash = (uint)Vector2.Dot(v, scale);
			hash += uvHash * count++;
			}

		// TODO: Hash for skinned meshes, bones, ... maybe?
		// mesh.boneWeights
		// mesh.bindposesSS

		return hash;
		}


	// Source:
	// http://answers.unity3d.com/questions/7789/calculating-tangents-vector4.html

	public static void CalculateMeshTangents (Mesh mesh)
		{
		//speed up math by copying the mesh arrays
		int[] triangles = mesh.triangles;
		Vector3[] vertices = mesh.vertices;
		Vector3[] normals = mesh.normals;
		Vector2[] uv = mesh.uv;

		bool hasUVs = uv.Length > 0;

		//variable definitions
		int triangleCount = triangles.Length;
		int vertexCount = vertices.Length;

		Vector3[] tan1 = new Vector3[vertexCount];
		Vector3[] tan2 = new Vector3[vertexCount];

		Vector4[] tangents = new Vector4[vertexCount];

		for (long a = 0; a < triangleCount; a += 3)
			{
			long i1 = triangles[a + 0];
			long i2 = triangles[a + 1];
			long i3 = triangles[a + 2];

			Vector3 v1 = vertices[i1];
			Vector3 v2 = vertices[i2];
			Vector3 v3 = vertices[i3];

			Vector2 w1 = Vector2.zero;
			Vector2 w2 = Vector2.zero;
			Vector2 w3 = Vector2.zero;

			if (hasUVs)
				{
				w1 = uv[i1];
				w2 = uv[i2];
				w3 = uv[i3];
				}

			float x1 = v2.x - v1.x;
			float x2 = v3.x - v1.x;
			float y1 = v2.y - v1.y;
			float y2 = v3.y - v1.y;
			float z1 = v2.z - v1.z;
			float z2 = v3.z - v1.z;

			float s1 = w2.x - w1.x;
			float s2 = w3.x - w1.x;
			float t1 = w2.y - w1.y;
			float t2 = w3.y - w1.y;

			float r = 1.0f / (s1 * t2 - s2 * t1);

			Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
			Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

			tan1[i1] += sdir;
			tan1[i2] += sdir;
			tan1[i3] += sdir;

			tan2[i1] += tdir;
			tan2[i2] += tdir;
			tan2[i3] += tdir;
			}

		for (long a = 0; a < vertexCount; ++a)
			{
			Vector3 n = normals[a];
			Vector3 t = tan1[a];

			Vector3.OrthoNormalize(ref n, ref t);
			tangents[a].x = t.x;
			tangents[a].y = t.y;
			tangents[a].z = t.z;

			tangents[a].w = (Vector3.Dot(Vector3.Cross(n, t), tan2[a]) < 0.0f) ? -1.0f : 1.0f;
			}

		mesh.tangents = tangents;
		}


	// Animation manipulation functions ------------------------------------------------------------


	void ProcessClipRotation (AnimationClip clip, string objectPathName)
		{
		// Access the rotation curves for the given object

		EditorCurveBinding bindX = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalRotation.x");
		EditorCurveBinding bindY = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalRotation.y");
		EditorCurveBinding bindZ = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalRotation.z");
		EditorCurveBinding bindW = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalRotation.w");

		AnimationCurve curveX = AnimationUtility.GetEditorCurve(clip, bindX);
		AnimationCurve curveY = AnimationUtility.GetEditorCurve(clip, bindY);
		AnimationCurve curveZ = AnimationUtility.GetEditorCurve(clip, bindZ);
		AnimationCurve curveW = AnimationUtility.GetEditorCurve(clip, bindW);

		if (curveX == null || curveY == null || curveZ == null || curveW == null)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have correct rotation curves");
			return;
			}

		int keyframes = curveX.length;
		if (curveY.length != keyframes || curveZ.length != keyframes || curveW.length != keyframes)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have the same keyframes for XYZW rotation curves. " +
			        "x: " + curveX.length + " y:" + curveY.length + " z:" + curveZ.length + " w:" + curveW.length);
			return;
			}

		if (keyframes == 0) return;

		bool isFirstLevel = objectPathName.IndexOf('/') < 0;

		// Retrieve the keyframes of the rotation curves

		Keyframe[] keysX = curveX.keys;
		Keyframe[] keysY = curveY.keys;
		Keyframe[] keysZ = curveZ.keys;
		Keyframe[] keysW = curveW.keys;

		float s45 = Mathf.Sin(Mathf.PI/4.0f);

		for (int k=0; k<keyframes; k++)
			{
			Quaternion rot = new Quaternion(keysX[k].value, keysY[k].value, keysZ[k].value, keysW[k].value);

			// Convert rotation from Blender (Right-handed) to Unity (Left-handed).
			// Must have in mind the X+90 rotation in the first level objects.

			if (isFirstLevel)
				{
				// This is the same as rot = rot * Quaternion.AngleAxis(90.0f, Vector3.right);
				// Idea courtesy of apatriarca (www.gamedev.net/topic/654682-quaternions-convert-between-left-right-handed-without-using-euler/)

				keysX[k].value = (rot.w + rot.x) * s45;
				keysY[k].value = (rot.y + rot.z) * s45;
				keysZ[k].value = (rot.z - rot.y) * s45;
				keysW[k].value = (rot.w - rot.x) * s45;

				// Perform the same operation with tangents

				float inTx = (keysW[k].inTangent + keysX[k].inTangent) * s45;
				float inTy = (keysY[k].inTangent + keysZ[k].inTangent) * s45;
				float inTz = (keysZ[k].inTangent - keysY[k].inTangent) * s45;
				float inTw = (keysW[k].inTangent - keysX[k].inTangent) * s45;
				float outTx = (keysW[k].outTangent + keysX[k].outTangent) * s45;
				float outTy = (keysY[k].outTangent + keysZ[k].outTangent) * s45;
				float outTz = (keysZ[k].outTangent - keysY[k].outTangent) * s45;
				float outTw = (keysW[k].outTangent - keysX[k].outTangent) * s45;

				keysX[k].inTangent = inTx;
				keysY[k].inTangent = inTy;
				keysZ[k].inTangent = inTz;
				keysW[k].inTangent = inTw;
				keysX[k].outTangent = outTx;
				keysY[k].outTangent = outTy;
				keysZ[k].outTangent = outTz;
				keysW[k].outTangent = outTw;
				}
			else
				{
				// Convert the rotation from Blender to Unity

				keysX[k].value = -rot.x;
				keysY[k].value = -rot.z;
				keysZ[k].value = rot.y;
				keysW[k].value = -rot.w;

				// Adjust and exchange tangents accordingly

				InvertTangents(ref keysX[k]);
				InvertTangents(ref keysW[k]);
				float inT = keysY[k].inTangent;
				float outT = keysY[k].outTangent;
				keysY[k].inTangent = -keysZ[k].inTangent;
				keysY[k].outTangent = -keysZ[k].outTangent;
				keysZ[k].inTangent = inT;
				keysZ[k].outTangent = outT;
				}

			// Turn around the forward axis if required

			if (m_zReverse)
				{
				keysX[k].value = -keysX[k].value;
				keysZ[k].value = -keysZ[k].value;

				InvertTangents(ref keysX[k]);
				InvertTangents(ref keysZ[k]);
				}
			}

		// Assign the fixed keyframes back to the rotation curves

		curveX.keys = keysX;
		curveY.keys = keysY;
		curveZ.keys = keysZ;
		curveW.keys = keysW;

		AnimationUtility.SetEditorCurve(clip, bindX, curveX);
		AnimationUtility.SetEditorCurve(clip, bindY, curveY);
		AnimationUtility.SetEditorCurve(clip, bindZ, curveZ);
		AnimationUtility.SetEditorCurve(clip, bindW, curveW);
		}


	void ProcessClipPosition (AnimationClip clip, string objectPathName)
		{
		// Access the position curves for the given object

		EditorCurveBinding bindX = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalPosition.x");
		EditorCurveBinding bindY = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalPosition.y");
		EditorCurveBinding bindZ = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalPosition.z");

		AnimationCurve curveX = AnimationUtility.GetEditorCurve(clip, bindX);
		AnimationCurve curveY = AnimationUtility.GetEditorCurve(clip, bindY);
		AnimationCurve curveZ = AnimationUtility.GetEditorCurve(clip, bindZ);

		if (curveX == null || curveY == null || curveZ == null)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have correct position curves");
			return;
			}

		int keyframes = curveX.length;
		if (curveY.length != keyframes || curveZ.length != keyframes)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have the same keyframes for XYZ position curves. " +
			        "x: " + curveX.length + " y:" + curveY.length + " z:" + curveZ.length);
			return;
			}

		if (keyframes == 0) return;

		bool isFirstLevel = objectPathName.IndexOf('/') < 0;

		// Retrieve the keyframes of the position curves

		Keyframe[] keysX = curveX.keys;
		Keyframe[] keysY = curveY.keys;
		Keyframe[] keysZ = curveZ.keys;

		for (int k=0; k<keyframes; k++)
			{
			Vector3 pos = new Vector3(keysX[k].value, keysY[k].value, keysZ[k].value);

			if (isFirstLevel)
				{
				if (m_zReverse)
					{
					// Turn around the local position

					pos = Quaternion.AngleAxis(180.0f, Vector3.up) * pos;

					// Tangents X and Z must be adjusted to match the new orientation

					InvertTangents(ref keysX[k]);
					InvertTangents(ref keysZ[k]);
					}
				}
			else
				{
				// Position in the 2nd level objects must compensante the X+90 rotation that
				// has been applied to 1st level.

				Quaternion q = Quaternion.AngleAxis(-90.0f, Vector3.right);
				if (m_zReverse) q = Quaternion.AngleAxis(180.0f, Vector3.up) * q;
				pos = q * pos;

				// Adjust the tangents of the Y and Z curves as result of the rotation X-90

				float inT = keysZ[k].inTangent;
				float outT = keysZ[k].outTangent;
				keysZ[k].inTangent = -keysY[k].inTangent;
				keysZ[k].outTangent = -keysY[k].outTangent;
				keysY[k].inTangent = inT;
				keysY[k].outTangent = outT;

				// If the model has been turned around (180º) the tangents for X and Z position
				// curves must be inverted as well.

				if (m_zReverse)
					{
					InvertTangents(ref keysX[k]);
					InvertTangents(ref keysZ[k]);
					}
				}

			if (m_floatFix)
				pos = FixVector3(pos, m_floatFixThreshold);

			keysX[k].value = pos.x;
			keysY[k].value = pos.y;
			keysZ[k].value = pos.z;
			}

		// Assign the fixed keyframes back to the position curves

		curveX.keys = keysX;
		curveY.keys = keysY;
		curveZ.keys = keysZ;

		AnimationUtility.SetEditorCurve(clip, bindX, curveX);
		AnimationUtility.SetEditorCurve(clip, bindY, curveY);
		AnimationUtility.SetEditorCurve(clip, bindZ, curveZ);
		}


	static void InvertTangents (ref Keyframe key)
		{
		key.inTangent = -key.inTangent;
		key.outTangent = -key.outTangent;
		}


	void ProcessClipScale (AnimationClip clip, string objectPathName)
		{
		// Access the scale curves for the given object

		EditorCurveBinding bindX = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalScale.x");
		EditorCurveBinding bindY = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalScale.y");
		EditorCurveBinding bindZ = EditorCurveBinding.FloatCurve(objectPathName, typeof(Transform), "m_LocalScale.z");

		AnimationCurve curveX = AnimationUtility.GetEditorCurve(clip, bindX);
		AnimationCurve curveY = AnimationUtility.GetEditorCurve(clip, bindY);
		AnimationCurve curveZ = AnimationUtility.GetEditorCurve(clip, bindZ);

		if (curveX == null || curveY == null || curveZ == null)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have correct scale curves");
			return;
			}

		int keyframes = curveX.length;
		if (curveY.length != keyframes || curveZ.length != keyframes)
			{
			LogInfo("WARNING: animated object [" + objectPathName + "] doesn't have the same keyframes for XYZ scale curves. " +
			        "x: " + curveX.length + " y:" + curveY.length + " z:" + curveZ.length);
			return;
			}

		if (keyframes == 0) return;

		// Scale curves require to swap the Y and Z axis only

		AnimationUtility.SetEditorCurve(clip, bindY, curveZ);
		AnimationUtility.SetEditorCurve(clip, bindZ, curveY);
		}


	// Log utilities -------------------------------------------------------------------------------


	static private string m_debugInfo;


	static private void LogInfo (string message)
		{
		m_debugInfo += message;
		m_debugInfo += "\n";
		}


	static private void LogFlush ()
		{
		if (m_debugInfo.Length > 0)
			{
			Debug.Log(m_debugInfo);
			m_debugInfo = "";
			}
		}


	static private void LogClear ()
		{
		m_debugInfo = "";
		}
	}
