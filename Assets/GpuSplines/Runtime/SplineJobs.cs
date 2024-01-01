﻿using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace PeDev.GpuSplines {
	[BurstCompile]
	internal struct AssignControlPointsJob : IJob {
		public int arrayLength;
		public bool insertFirstLastPoints;
		public SplineComponent component;
		
		[ReadOnly] 
		public NativeArray<float3> source;
		
		public NativeArray<float4> destination;
		
		
		public void Execute() {
			int dstOffset = insertFirstLastPoints ? component.startIndexControlPoint + 1 : component.startIndexControlPoint;
			for (int i = 0; i < arrayLength; i++) {
				float3 value = source[i];
				destination[dstOffset + i] = new float4(value.x, value.y, value.z, 0f);
			}
			
			if (insertFirstLastPoints) {
				int firstIndex = component.startIndexControlPoint;
				int lastIndex = component.endIndexControlPoint - 1;
				destination[firstIndex] =
					destination[firstIndex + 1] * 2 - destination[firstIndex + 2];
				destination[lastIndex] =
					destination[lastIndex - 1] * 2 - destination[lastIndex - 2];
			}
		}
	}
	
	[BurstCompile]
	internal struct CreateSplineMeshJob : IJobParallelFor {
		[ReadOnly] public NativeArray<SplineEntity> splineIndices;
		[ReadOnly] public NativeArray<SplineComponent> splineComponents;


		[WriteOnly, NativeDisableParallelForRestriction]
		public NativeArray<float3> vertices;

		[WriteOnly, NativeDisableParallelForRestriction]
		public NativeArray<int> triangles;

		[WriteOnly, NativeDisableParallelForRestriction]
		public NativeArray<Color32> colors;

		public void Execute(int jobIndex) {
			int s = jobIndex;
			SplineEntity entity = splineIndices[s];
			SplineComponent component = splineComponents[entity.id];


			int lineCount = component.numControlPoints - 3;
			int numVertices = component.numVertices;
			float invNumVertices = 1f / numVertices;

			int startIndexCp = component.startIndexControlPoint;
			int writeIndexVertices = component.startIndexVertices * 2;
			for (int l = 0; l < lineCount; l++) {
				float subInterval = 0f;
				for (int i = 0; i < component.numVerticesPerSegment; i++) {
					int index = startIndexCp + l;
					subInterval = (float)(i) / (component.numVerticesPerSegment - 1);
					float norm = ((l * component.numVerticesPerSegment) + i) * invNumVertices;

					// x : V texture coordinate
					// y : Spline interval t [0..1]
					// z : Index in the control point uniform
					vertices[writeIndexVertices] = new float3(norm, subInterval, index);
					vertices[writeIndexVertices + 1] = new float3(norm, subInterval, index);

					// Red = 0 : left vertex
					// Red = 255 : right vertex
					colors[writeIndexVertices] = new Color32(0, 0, 0, 0);
					colors[writeIndexVertices + 1] = new Color32(255, 0, 0, 0);

					writeIndexVertices += 2;
				}
			}

			int writeIndexTriangle = (component.startIndexVertices - s) * 6;
			for (int i = 0; i < component.numVertices - 1; i++) {
				int vertIndex = (component.startIndexVertices * 2) + i * 2;

				triangles[writeIndexTriangle++] = vertIndex;
				triangles[writeIndexTriangle++] = vertIndex + 2;
				triangles[writeIndexTriangle++] = vertIndex + 1;

				triangles[writeIndexTriangle++] = vertIndex + 1;
				triangles[writeIndexTriangle++] = vertIndex + 2;
				triangles[writeIndexTriangle++] = vertIndex + 3;
			}
		}
	}
	
	/// <summary>
	/// The segments of the spline between 2 control points.
	/// This is used for Graphics.DrawProcedural(). 
	/// </summary>
	internal struct ProceduralSegment
	{
		// index in the control point uniform.
		public uint index;
		// spline interval t [0..1]
		public float t;
		// V texture coordinate
		public float tex_v;
		// 0 = the end of the spline. 1 = Not end.
		public float isNotEnd;
	};
	
	[BurstCompile]
	internal struct CreateSplineProceduralDataJob : IJobParallelFor {
		[ReadOnly] public NativeArray<SplineEntity> splineIndices;
		[ReadOnly] public NativeArray<SplineComponent> splineComponents;


		[WriteOnly, NativeDisableParallelForRestriction]
		public NativeArray<ProceduralSegment> segmentBuffer;
		
		public void Execute(int jobIndex) {
			int s = jobIndex;
			SplineEntity entity = splineIndices[s];
			SplineComponent component = splineComponents[entity.id];


			int lineCount = component.numControlPoints - 3;
			int numVertices = component.numVertices;
			float invNumVertices = 1f / numVertices;

			int startIndexCp = component.startIndexControlPoint;
			int writeIndexVertices = component.startIndexVertices;
			for (int l = 0; l < lineCount; l++) {
				float subInterval = 0f;
				for (int i = 0; i < component.numVerticesPerSegment; i++) {
					int index = startIndexCp + l;
					subInterval = (float)(i) / (component.numVerticesPerSegment - 1);
					float norm = ((l * component.numVerticesPerSegment) + i) * invNumVertices;

					segmentBuffer[writeIndexVertices] = new ProceduralSegment() {
						t = subInterval,
						index = (uint)index,
						tex_v = norm,
						isNotEnd = (l < lineCount - 1 || i < component.numVerticesPerSegment - 1) ? 1.0f : 0.0f
					};
					writeIndexVertices += 1;
				}
			}
		}
	}
	
	[BurstCompile]
	internal struct CalculateBoundJob : IJob
	{
		[ReadOnly] public NativeArray<SplineEntity> splineIndices;
		[ReadOnly] public NativeArray<SplineComponent> splineComponents;
		[ReadOnly] public NativeArray<float4> controlPoints;

		public int splineCount;

		[WriteOnly] public NativeArray<Vector3> result;

		public void Execute() {
			float3 minPos = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
			float3 maxPos = new float3(float.MinValue, float.MinValue, float.MinValue);
			
			for (int i = 0; i < splineCount; i++) {
				var entity = splineIndices[i];
				// skip the first and the last control points in each splines.
				int startIndex = splineComponents[entity.id].startIndexControlPoint + 1;
				int count = splineComponents[entity.id].numControlPoints - 2;
				for (int cp = 0; cp < count; cp++) {
					float4 pos = controlPoints[startIndex + cp];
					if (pos.x < minPos.x)
						minPos.x = pos.x;
					if (pos.x > maxPos.x)
						maxPos.x = pos.x;
				
					if (pos.y < minPos.y)
						minPos.y = pos.y;
					if (pos.y > maxPos.y)
						maxPos.y = pos.y;
				
					if (pos.z < minPos.z)
						minPos.z = pos.z;
					if (pos.z > maxPos.z)
						maxPos.z = pos.z;
				}
			}
			
			result[0] = minPos;
			result[1] = maxPos;
		}
	}
}