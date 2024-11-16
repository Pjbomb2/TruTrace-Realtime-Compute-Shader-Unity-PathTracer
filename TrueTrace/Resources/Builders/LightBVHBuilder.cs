using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using CommonVars;
using System; 
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace TrueTrace {
    [System.Serializable]
    public unsafe class LightBVHBuilder {
        void OnDestroy() {
            Debug.Log("EEE");
        }
        public NodeBounds ParentBound;
        public struct DirectionCone {
            public Vector3 W;
            public float cosTheta;

            public DirectionCone(Vector3 w, float cosTheta) {
                W = w;
                this.cosTheta = cosTheta;
            }
        }

        private float luminance(float r, float g, float b) { return 0.299f * r + 0.587f * g + 0.114f * b; }

        float Dot(ref Vector3 A, ref Vector3 B) {return A.x * B.x + A.y * B.y + A.z * B.z;}
        float Dot(Vector3 A) {return A.x * A.x + A.y * A.y + A.z * A.z;}
        float Length(Vector3 A) {return (float)System.Math.Sqrt(Dot(A));}

        public bool IsEmpty(ref LightBounds Cone) {return Cone.w == Vector3.zero;}


        private float AngleBetween(Vector3 v1, Vector3 v2) {
            if(Dot(ref v1, ref v2) < 0) return 3.14159f - 2.0f * (float)System.Math.Asin(Length(v1 + v2) / 2.0f);
            else return 2.0f * (float)System.Math.Asin(Length(v2 - v1) / 2.0f);
        }

        private Matrix4x4 Rotate(float sinTheta, float cosTheta, Vector3 axis) {
            Vector3 a = (axis).normalized;
            Matrix4x4 m = Matrix4x4.identity;
            m[0,0] = a.x * a.x + (1 - a.x * a.x) * cosTheta;
            m[0,1] = a.x * a.y * (1 - cosTheta) - a.z * sinTheta;
            m[0,2] = a.x * a.z * (1 - cosTheta) + a.y * sinTheta;
            m[0,3] = 0;

            m[1,0] = a.x * a.y * (1 - cosTheta) + a.z * sinTheta;
            m[1,1] = a.y * a.y + (1 - a.y * a.y) * cosTheta;
            m[1,2] = a.y * a.z * (1 - cosTheta) - a.x * sinTheta;
            m[1,3] = 0;

            m[2,0] = a.x * a.z * (1 - cosTheta) - a.y * sinTheta;
            m[2,1] = a.y * a.z * (1 - cosTheta) + a.x * sinTheta;
            m[2,2] = a.z * a.z + (1 - a.z * a.z) * cosTheta;
            m[2,3] = 0;

            return m * m.transpose;
        }

        private Matrix4x4 Rotate(float Theta, Vector3 axis) {

            return Rotate((float)System.Math.Sin(Theta),(float)System.Math.Cos(Theta), axis);
        }

        public void UnionCone(ref LightBounds A, ref LightBounds B) {
            if(IsEmpty(ref A)) {A.w = B.w; A.cosTheta_o = B.cosTheta_o; return;}
            if(IsEmpty(ref B)) return;

            float theta_a = (float)System.Math.Acos(Mathf.Clamp(A.cosTheta_o,-1.0f, 1.0f));
            float theta_b = (float)System.Math.Acos(Mathf.Clamp(B.cosTheta_o,-1.0f, 1.0f));
            float theta_d = AngleBetween(A.w, B.w);
            if(System.Math.Min(theta_d + theta_b, 3.14159f) <= theta_a) return;
            if(System.Math.Min(theta_d + theta_a, 3.14159f) <= theta_b) {A.w = B.w; A.cosTheta_o = B.cosTheta_o; return;}

            float theta_o = (theta_a + theta_d + theta_b) / 2.0f;
            if(theta_o >= 3.14159f) {A.w = new Vector3(0,0,0); A.cosTheta_o = -1; return;}

            float theta_r = theta_o - theta_a;
            Vector3 wr = Vector3.Cross(A.w, B.w);
            if(Vector3.Dot(wr, wr) == 0) {A.w = new Vector3(0,0,0); A.cosTheta_o = -1; return;}
            A.w = Rotate(theta_r, wr) * A.w;
            A.cosTheta_o =(float)System.Math.Cos(theta_o);
        }



    

        private void Union(ref LightBounds A, LightBounds B) {
            if(A.phi == 0) {A = B; return;}
            if(B.phi == 0) return;
            UnionCone(ref A, ref B);
            A.cosTheta_e = System.Math.Min(A.cosTheta_e, B.cosTheta_e);
            A.b.Extend(ref B.b);
            A.phi += B.phi;
            A.LightCount += B.LightCount;
        }

        private float surface_area(AABB aabb) {
            Vector3 sizes = aabb.BBMax - aabb.BBMin;
            return 2.0f * ((sizes.x * sizes.y) + (sizes.x * sizes.z) + (sizes.y * sizes.z)); 
        }

        private float EvaluateCost(ref LightBounds b, float Kr, int dim) {
            float theta_o = (float)System.Math.Acos(b.cosTheta_o);
            float theta_e = (float)System.Math.Acos(b.cosTheta_e);
            float theta_w = System.Math.Min(theta_o + theta_e, 3.14159f);
            float sinTheta_o = Mathf.Sqrt(1.0f - b.cosTheta_o * b.cosTheta_o);
            float M_omega = 2.0f * 3.14159f * (1.0f - b.cosTheta_o) +
                            3.14159f / 2.0f *
                                (2.0f * theta_w * sinTheta_o -(float)System.Math.Cos(theta_o - 2.0f * theta_w) -
                                 2.0f * theta_o * sinTheta_o + b.cosTheta_o);

            float Radius = Vector3.Distance((b.b.BBMax + b.b.BBMin) / 2.0f, b.b.BBMax);
            float SA = 4.0f * Mathf.PI * Radius * Radius;
            
            return b.phi * M_omega * Kr * surface_area(b.b) / (float)Mathf.Max(b.LightCount, 1);
        }

        private LightBounds* LightTris;
        private NodeBounds* nodes2;
#if DontUseSGTree
        public CompactLightBVHData[] nodes;
#else
        private CompactLightBVHData[] nodes;
        public GaussianTreeNode[] SGTree;
#endif
        private int* DimensionedIndices;
        public int PrimCount;
        private float* SAH;
        private bool* indices_going_left;
        private int* temp;
        public int[] FinalIndices;

        public NativeArray<LightBounds> LightTrisArray;
        public NativeArray<NodeBounds> nodes2Array;
        public NativeArray<int> DimensionedIndicesArray;
        public NativeArray<int> tempArray;
        public NativeArray<bool> indices_going_left_array;
        public NativeArray<float> CentersX;
        public NativeArray<float> CentersY;
        public NativeArray<float> CentersZ;
        public NativeArray<float> SAHArray;

        public struct ObjectSplit {
            public int index;
            public float cost;
            public int dimension;
            public LightBounds aabb_left;
            public LightBounds aabb_right;
        }
        private ObjectSplit split = new ObjectSplit();


        ObjectSplit partition_sah(int first_index, int index_count, AABB parentBounds) {
            split.cost = float.MaxValue;
            split.index = -1;
            split.dimension = -1;

            LightBounds aabb_left = new LightBounds();
            LightBounds aabb_right = new LightBounds();
            int Offset;
            Vector3 Diagonal = parentBounds.BBMax - parentBounds.BBMin;
            float Kr1 = System.Math.Max(System.Math.Max(Diagonal.x, Diagonal.y), Diagonal.z);
            for(int dimension = 0; dimension < 3; dimension++) {
                float Kr = Kr1 / Diagonal[dimension];
                Offset = PrimCount * dimension + first_index;
                aabb_left = LightTris[DimensionedIndices[Offset]];
                SAH[1] = EvaluateCost(ref aabb_left, Kr, dimension);
                for(int i = 2; i < index_count; i++) {
                    Union(ref aabb_left, LightTris[DimensionedIndices[Offset + i - 1]]);

                    SAH[i] = EvaluateCost(ref aabb_left, Kr, dimension) * (float)i;
                }

                {
                    aabb_right = LightTris[DimensionedIndices[Offset + index_count - 1]];
                    float cost = SAH[index_count - 1] + EvaluateCost(ref aabb_right, Kr, dimension) * (float)(index_count - (index_count - 1));

                    if(cost != 0)
                    if(cost <= split.cost) {
                        split.cost = cost;
                        split.index = first_index + index_count - 1;
                        split.dimension = dimension;
                        split.aabb_right = aabb_right;
                    }
                }


                for(int i = index_count - 2; i > 0; i--) {
                    Union(ref aabb_right, LightTris[DimensionedIndices[Offset + i]]);

                    float cost = SAH[i] + EvaluateCost(ref aabb_right, Kr, dimension) * (float)(index_count - i);

                    if(cost != 0)
                    if(cost <= split.cost) {
                        split.cost = cost;
                        split.index = first_index + i;
                        split.dimension = dimension;
                        split.aabb_right = aabb_right;
                    }
                }
            }
            if(split.cost == float.MaxValue) split.dimension = 0;
            Offset = split.dimension * PrimCount;
            if(split.cost == float.MaxValue) {
                split.index = first_index + (index_count) / 2;
                for(int i = split.index; i < index_count + first_index; i++) {
                    Union(ref split.aabb_right, LightTris[DimensionedIndices[Offset + i]]);
                }
            }
            split.aabb_left = LightTris[DimensionedIndices[Offset + first_index]];
            for(int i = first_index + 1; i < split.index; i++) Union(ref split.aabb_left, LightTris[DimensionedIndices[Offset + i]]);
            return split;
        }
        public int MaxDepth;
        void BuildRecursive(int nodesi, ref int node_index, int first_index, int index_count, int Depth) {
            if(index_count == 1) {
                nodes2[nodesi].left = first_index;
                nodes2[nodesi].isLeaf = 1;
                MaxDepth = System.Math.Max(Depth, MaxDepth);
                return;
            }
            ObjectSplit split = partition_sah(first_index, index_count, nodes2[nodesi].aabb.b);
            int Offset = split.dimension * PrimCount;
            int IndexEnd = first_index + index_count;
            for(int i = first_index; i < IndexEnd; i++) indices_going_left[DimensionedIndices[Offset + i]] = i < split.index;

            for(int dim = 0; dim < 3; dim++) {
                if(dim == split.dimension) continue;

                int index;
                int left = 0;
                int right = split.index - first_index;
                Offset = dim * PrimCount;
                for(int i = first_index; i < IndexEnd; i++) {
                    index = DimensionedIndices[Offset + i];
                    temp[indices_going_left[index] ? (left++) : (right++)] = index;
                }
          
                NativeArray<int>.Copy(tempArray, 0, DimensionedIndicesArray, Offset + first_index, index_count);
            }
            nodes2[nodesi].left = node_index;

            nodes2[nodes2[nodesi].left].aabb = split.aabb_left;
            nodes2[nodes2[nodesi].left + 1].aabb = split.aabb_right;
            node_index += 2;

            BuildRecursive(nodes2[nodesi].left, ref node_index,first_index,split.index - first_index, Depth + 1);
            BuildRecursive(nodes2[nodesi].left + 1, ref node_index,first_index + split.index - first_index,first_index + index_count - split.index, Depth + 1);
        }
        public ComputeBuffer[] WorkingSet;

  
        private float AreaOfTriangle(Vector3 pt1, Vector3 pt2, Vector3 pt3)
        {
            float a = Vector3.Distance(pt1, pt2);
            float b = Vector3.Distance(pt2, pt3);
            float c = Vector3.Distance(pt3, pt1);
            float s = (a + b + c) / 2.0f;
            return Mathf.Sqrt(s * (s - a) * (s - b) * (s - c));
        }

        public List<int>[] Set;
        void Refit(int Depth, int CurrentIndex) {
            if(nodes2[CurrentIndex].aabb.cosTheta_e == 0) return;
            Set[Depth].Add(CurrentIndex);
            if(nodes2[CurrentIndex].isLeaf == 1) return;
            Refit(Depth + 1, nodes2[CurrentIndex].left);
            Refit(Depth + 1, nodes2[CurrentIndex].left + 1);
        }

        private void Refit2(int Depth, int CurrentIndex) {
            if((2.0f * ((float)(nodes[CurrentIndex].cosTheta_oe >> 16) / 32767.0f) - 1.0f) == 0) return;
            Set[Depth].Add(CurrentIndex);
            if(nodes[CurrentIndex].left < 0) return;
            Refit2(Depth + 1, nodes[CurrentIndex].left);
            Refit2(Depth + 1, nodes[CurrentIndex].left + 1);
        }

        float expm1_over_x(float x)
        {
            float u = Mathf.Exp(x);

            if (u == 1.0f)
            {
                return 1.0f;
            }

            float y = u - 1.0f;

            if (Mathf.Abs(x) < 1.0f)
            {
                return y / Mathf.Log(u);
            }

            return y / x;
        }

        float SGIntegral(float sharpness)
        {
            return 4.0f * Mathf.PI * expm1_over_x(-2.0f * sharpness);
        }

        public LightBVHBuilder() {}


        public unsafe LightBVHBuilder(List<LightTriData> Tris, List<Vector3> Norms, float phi, List<float> LuminanceWeights) {//need to make sure incomming is transformed to world space already
            PrimCount = Tris.Count;          
            MaxDepth = 0;
            tempArray = new NativeArray<int>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            SAHArray = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            indices_going_left_array = new NativeArray<bool>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            DimensionedIndicesArray = new NativeArray<int>(PrimCount * 3, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersX = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersY = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersZ = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            nodes2Array = new NativeArray<NodeBounds>(PrimCount * 2, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            LightTrisArray = new NativeArray<LightBounds>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);


            nodes2 = (NodeBounds*)NativeArrayUnsafeUtility.GetUnsafePtr(nodes2Array);
            LightTris = (LightBounds*)NativeArrayUnsafeUtility.GetUnsafePtr(LightTrisArray);
            float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersX);
            float* ptr1 = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersY);
            float* ptr2 = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersZ);
            indices_going_left = (bool*)NativeArrayUnsafeUtility.GetUnsafePtr(indices_going_left_array);
            DimensionedIndices = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(DimensionedIndicesArray);
            temp = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(tempArray);
            SAH = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(SAHArray);

            FinalIndices = new int[PrimCount];

            for(int i = 0; i < PrimCount; i++) {
                AABB TriAABB = new AABB();
                TriAABB.init();
                TriAABB.Extend(Tris[i].pos0);
                TriAABB.Extend(Tris[i].pos0 + Tris[i].posedge1);
                TriAABB.Extend(Tris[i].pos0 + Tris[i].posedge2);
                TriAABB.Validate(new Vector3(0.0001f,0.0001f,0.0001f));
                DirectionCone tricone = new DirectionCone(-Norms[i], 1);
                float ThisPhi = AreaOfTriangle(Tris[i].pos0, Tris[i].pos0 + Tris[i].posedge1, Tris[i].pos0 + Tris[i].posedge2) * LuminanceWeights[i];
                LightBounds TempBound = new LightBounds(TriAABB, tricone.W, ThisPhi, tricone.cosTheta,(float)System.Math.Cos(3.14159f / 2.0f), 1, 0);
                LightTris[i] = TempBound;
                FinalIndices[i] = i;
                ptr[i] = (TriAABB.BBMax.x - TriAABB.BBMin.x) / 2.0f + TriAABB.BBMin.x;
                ptr1[i] = (TriAABB.BBMax.y - TriAABB.BBMin.y) / 2.0f + TriAABB.BBMin.y;
                ptr2[i] = (TriAABB.BBMax.z - TriAABB.BBMin.z) / 2.0f + TriAABB.BBMin.z;
                Union(ref nodes2[0].aabb, TempBound);
            }

            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr[s1] - ptr[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersX.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, 0, PrimCount);
            for(int i = 0; i < PrimCount; i++) FinalIndices[i] = i;
            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr1[s1] - ptr1[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersY.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, PrimCount, PrimCount);
            for(int i = 0; i < PrimCount; i++) FinalIndices[i] = i;
            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr2[s1] - ptr2[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersZ.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, PrimCount * 2, PrimCount);
            CommonFunctions.DeepClean(ref FinalIndices);

            int nodeIndex = 2;
            BuildRecursive(0, ref nodeIndex,0,PrimCount, 1);
            indices_going_left_array.Dispose();
            tempArray.Dispose();
            SAHArray.Dispose();

            // NodeBounds[] TempNodes = new NodeBounds[nodeIndex];
            // for(int i = 0; i < nodeIndex; i++) {
                // TempNodes[i] = nodes2[i];
            // }
            // nodes2 = new NodeBounds[nodeIndex];
            // nodes2 = TempNodes;
            for(int i = 0; i < PrimCount * 2; i++) {
                if(nodes2[i].isLeaf == 1) {
                    nodes2[i].left = DimensionedIndices[nodes2[i].left];
                }
            }
            DimensionedIndicesArray.Dispose();
            nodes = new CompactLightBVHData[PrimCount * 2];
            for(int i = 0; i < PrimCount * 2; i++) {
                CompactLightBVHData TempNode = new CompactLightBVHData();
                TempNode.BBMax = nodes2[i].aabb.b.BBMax;
                TempNode.BBMin = nodes2[i].aabb.b.BBMin;
                TempNode.w = CommonFunctions.PackOctahedral(nodes2[i].aabb.w);
                TempNode.phi = nodes2[i].aabb.phi;
                TempNode.cosTheta_oe = ((uint)Mathf.Floor(32767.0f * ((nodes2[i].aabb.cosTheta_o + 1.0f) / 2.0f))) | ((uint)Mathf.Floor(32767.0f * ((nodes2[i].aabb.cosTheta_e + 1.0f) / 2.0f)) << 16);
                if(nodes2[i].isLeaf == 1) {
                    TempNode.left = (-nodes2[i].left) - 1;
                } else {
                    TempNode.left = nodes2[i].left;
                }
                nodes[i] = TempNode;
            }
            ParentBound = nodes2[0];
            // ParentBound.aabb.phi /= (float)Mathf.Max(ParentBound.aabb.LightCount, 1);
            nodes2Array.Dispose();
            LightTrisArray.Dispose();
#if !DontUseSGTree
            {
                SGTree = new GaussianTreeNode[nodes.Length];
                Set = new List<int>[MaxDepth];
                for(int i = 0; i < MaxDepth; i++) Set[i] = new List<int>();
                Refit2(0, 0);
                for(int i = MaxDepth - 1; i >= 0; i--) {
                    int SetCount = Set[i].Count;
                    for(int j = 0; j < SetCount; j++) {
                        GaussianTreeNode TempNode = new GaussianTreeNode();
                        int WriteIndex = Set[i][j];
                        CompactLightBVHData LBVHNode = nodes[WriteIndex];
                        Vector3 V;
                        Vector3 mean;
                        float variance;
                        float intensity;
                        float radius;
                        if(LBVHNode.left < 0) {
                            LightTriData ThisLight = Tris[-(LBVHNode.left+1)];

                            float area = AreaOfTriangle(ThisLight.pos0, ThisLight.pos0 + ThisLight.posedge1, ThisLight.pos0 + ThisLight.posedge2);

                            intensity = ThisLight.SourceEnergy * area;
                            V = 0.5f * -Norms[-(LBVHNode.left+1)];//(Vector3.Cross(ThisLight.posedge1.normalized, ThisLight.posedge2.normalized).normalized);
                            mean = (ThisLight.pos0 + (ThisLight.pos0 + ThisLight.posedge1) + (ThisLight.pos0 + ThisLight.posedge2)) / 3.0f;
                            variance = (Vector3.Dot(ThisLight.posedge1, ThisLight.posedge1) + Vector3.Dot(ThisLight.posedge2, ThisLight.posedge2) - Vector3.Dot(ThisLight.posedge1, ThisLight.posedge2)) / 18.0f;
                            radius = Mathf.Max(Mathf.Max(Vector3.Distance(mean, ThisLight.pos0), Vector3.Distance(mean, ThisLight.pos0 + ThisLight.posedge1)), Vector3.Distance(mean, ThisLight.pos0 + ThisLight.posedge2));
                        } else {
                            GaussianTreeNode LeftNode = SGTree[nodes[WriteIndex].left];    
                            GaussianTreeNode RightNode = SGTree[nodes[WriteIndex].left + 1];

                            float phi_left = LeftNode.intensity;    
                            float phi_right = RightNode.intensity;    
                            float w_left = phi_left / (phi_left + phi_right);
                            float w_right = phi_right / (phi_left + phi_right);
                            
                            V = w_left * LeftNode.axis + w_right * RightNode.axis;

                            mean = w_left * LeftNode.S.Center + w_right * RightNode.S.Center;
                            variance = w_left * LeftNode.variance + w_right * RightNode.variance + w_left * w_right * Vector3.Dot(LeftNode.S.Center - RightNode.S.Center, LeftNode.S.Center - RightNode.S.Center);

                            intensity = LeftNode.intensity + RightNode.intensity;
                            radius = Mathf.Max(Vector3.Distance(mean, LeftNode.S.Center) + LeftNode.S.Radius, Vector3.Distance(mean, RightNode.S.Center) + RightNode.S.Radius);
                        }
                        TempNode.sharpness = ((3.0f * Vector3.Distance(Vector3.zero, V) - Mathf.Pow(Vector3.Distance(Vector3.zero, V), 3))) / (1.0f - Mathf.Pow(Vector3.Distance(Vector3.zero, V), 2));
                        TempNode.axis = V;
                        TempNode.S.Center = mean;
                        TempNode.variance = variance;
                        TempNode.intensity = intensity;
                        TempNode.S.Radius = radius;

                        TempNode.left = LBVHNode.left;
                        SGTree[WriteIndex] = TempNode;
                    }
                }
            }

            // SGTree[0].intensity /= (float)Mathf.Max(ParentBound.aabb.LightCount, 1);
            CommonFunctions.DeepClean(ref nodes);
#endif
        }



        public LightBVHBuilder(LightBounds[] Tris,ref GaussianTreeNode[] SGTree, LightBVHTransform[] LightBVHTransforms, GaussianTreeNode[] SGTreeNodes) {//need to make sure incomming is transformed to world space already
            PrimCount = Tris.Length;          
            MaxDepth = 0;
            
            LightTrisArray = new NativeArray<LightBounds>(Tris, Unity.Collections.Allocator.TempJob);
            LightTris = (LightBounds*)NativeArrayUnsafeUtility.GetUnsafePtr(LightTrisArray);
            tempArray = new NativeArray<int>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            SAHArray = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            indices_going_left_array = new NativeArray<bool>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            DimensionedIndicesArray = new NativeArray<int>(PrimCount * 3, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersX = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersY = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            CentersZ = new NativeArray<float>(PrimCount, Unity.Collections.Allocator.TempJob, NativeArrayOptions.UninitializedMemory);


            nodes2Array = new NativeArray<NodeBounds>(PrimCount * 2, Unity.Collections.Allocator.TempJob, NativeArrayOptions.ClearMemory);
            nodes2 = (NodeBounds*)NativeArrayUnsafeUtility.GetUnsafePtr(nodes2Array);
            float* ptr = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersX);
            float* ptr1 = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersY);
            float* ptr2 = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(CentersZ);
            indices_going_left = (bool*)NativeArrayUnsafeUtility.GetUnsafePtr(indices_going_left_array);
            DimensionedIndices = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(DimensionedIndicesArray);
            temp = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(tempArray);
            SAH = (float*)NativeArrayUnsafeUtility.GetUnsafePtr(SAHArray);


            FinalIndices = new int[PrimCount];

            for(int i = 0; i < PrimCount; i++) {
                FinalIndices[i] = i;
                ptr[i] = (Tris[i].b.BBMax.x - Tris[i].b.BBMin.x) / 2.0f + Tris[i].b.BBMin.x;
                ptr1[i] = (Tris[i].b.BBMax.y - Tris[i].b.BBMin.y) / 2.0f + Tris[i].b.BBMin.y;
                ptr2[i] = (Tris[i].b.BBMax.z - Tris[i].b.BBMin.z) / 2.0f + Tris[i].b.BBMin.z;
                Union(ref nodes2[0].aabb, Tris[i]);
            }

            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr[s1] - ptr[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersX.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, 0, PrimCount);
            for(int i = 0; i < PrimCount; i++) FinalIndices[i] = i;
            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr1[s1] - ptr1[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersY.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, PrimCount, PrimCount);
            for(int i = 0; i < PrimCount; i++) FinalIndices[i] = i;
            System.Array.Sort(FinalIndices, (s1,s2) => {var sign = ptr2[s1] - ptr2[s2]; return sign < 0 ? -1 : (sign == 0 ? 0 : 1);});
            CentersZ.Dispose();
            NativeArray<int>.Copy(FinalIndices, 0, DimensionedIndicesArray, PrimCount * 2, PrimCount);
            CommonFunctions.DeepClean(ref FinalIndices);

            int nodeIndex = 2;
            BuildRecursive(0, ref nodeIndex,0,PrimCount, 1);
            indices_going_left_array.Dispose();
            tempArray.Dispose();
            SAHArray.Dispose();

            for(int i = 0; i < PrimCount * 2; i++) {
                if(nodes2[i].isLeaf == 1) {
                    nodes2[i].left = DimensionedIndices[nodes2[i].left];
                    // nodes2[i].left = Offsets[nodes2[i].left];
                }
            }
            Set = new List<int>[MaxDepth];
            WorkingSet = new ComputeBuffer[MaxDepth];
            for(int i = 0; i < MaxDepth; i++) Set[i] = new List<int>();
            Refit(0, 0);
            for(int i = 0; i < MaxDepth; i++) {
                WorkingSet[i] = new ComputeBuffer(Set[i].Count, 4);
                WorkingSet[i].SetData(Set[i]);
            }
            nodes = new CompactLightBVHData[PrimCount * 2];
            for(int i = 0; i < PrimCount * 2; i++) {
                CompactLightBVHData TempNode = new CompactLightBVHData();
                TempNode.BBMax = nodes2[i].aabb.b.BBMax;
                TempNode.BBMin = nodes2[i].aabb.b.BBMin;
                TempNode.w = CommonFunctions.PackOctahedral(nodes2[i].aabb.w);
                TempNode.phi = nodes2[i].aabb.phi;
                TempNode.cosTheta_oe = ((uint)Mathf.Floor(32767.0f * ((nodes2[i].aabb.cosTheta_o + 1.0f) / 2.0f))) | ((uint)Mathf.Floor(32767.0f * ((nodes2[i].aabb.cosTheta_e + 1.0f) / 2.0f)) << 16);
                if(nodes2[i].isLeaf == 1) {
                    TempNode.left = (-nodes2[i].left) - 1;
                } else {
                    TempNode.left = nodes2[i].left;
                }
                nodes[i] = TempNode;
            }
            DimensionedIndicesArray.Dispose();
            LightTrisArray.Dispose();
            nodes2Array.Dispose();

#if !DontUseSGTree
            {
                for(int i = 0; i < MaxDepth; i++) Set[i] = new List<int>();
                Refit2(0, 0);
                for(int i = MaxDepth - 1; i >= 0; i--) {
                    int SetCount = Set[i].Count;
                    for(int j = 0; j < SetCount; j++) {
                        GaussianTreeNode TempNode = new GaussianTreeNode();
                        int WriteIndex = Set[i][j];
                        CompactLightBVHData LBVHNode = nodes[WriteIndex];
                        Vector3 V;
                        Vector3 mean;
                        float variance;
                        float intensity;
                        float radius;
                        if(LBVHNode.left < 0) {
                            TempNode = SGTreeNodes[-(LBVHNode.left+1)];
                            Vector3 ExtendedCenter = CommonFunctions.ToVector3(LightBVHTransforms[-(LBVHNode.left+1)].Transform * CommonFunctions.ToVector4(TempNode.S.Center + new Vector3(TempNode.S.Radius, 0, 0), 1));
                            Vector3 center = CommonFunctions.ToVector3(LightBVHTransforms[-(LBVHNode.left+1)].Transform * CommonFunctions.ToVector4(TempNode.S.Center, 1));
                            Vector3 Axis = CommonFunctions.ToVector3(LightBVHTransforms[-(LBVHNode.left+1)].Transform * CommonFunctions.ToVector4(TempNode.axis, 0));
                            float Scale = Vector3.Distance(center, ExtendedCenter) / TempNode.S.Radius;
                            TempNode.axis = Axis;
                            TempNode.S.Center = center;
                            TempNode.variance *= Scale;
                            TempNode.S.Radius *= Scale;
                            TempNode.intensity *= Scale;
                        } else {
                            GaussianTreeNode LeftNode = SGTree[nodes[WriteIndex].left];    
                            GaussianTreeNode RightNode = SGTree[nodes[WriteIndex].left + 1];

                            float phi_left = LeftNode.intensity;    
                            float phi_right = RightNode.intensity;    
                            float w_left = phi_left / (phi_left + phi_right);
                            float w_right = phi_right / (phi_left + phi_right);
                            
                            V = w_left * LeftNode.axis + w_right * RightNode.axis;//may be wrong, paper uses BAR_V(BAR_axis here), not just normalized V/axis

                            mean = w_left * LeftNode.S.Center + w_right * RightNode.S.Center;
                            variance = w_left * LeftNode.variance + w_right * RightNode.variance + w_left * w_right * Vector3.Dot(LeftNode.S.Center - RightNode.S.Center, LeftNode.S.Center - RightNode.S.Center);

                            intensity = LeftNode.intensity + RightNode.intensity;
                            radius = Mathf.Max(Vector3.Distance(mean, LeftNode.S.Center) + LeftNode.S.Radius, Vector3.Distance(mean, RightNode.S.Center) + RightNode.S.Radius);
                            TempNode.sharpness = ((3.0f * Vector3.Distance(Vector3.zero, V) - Mathf.Pow(Vector3.Distance(Vector3.zero, V), 3))) / (1.0f - Mathf.Pow(Vector3.Distance(Vector3.zero, V), 2));
                            TempNode.axis = V;
                            TempNode.S.Center = mean;
                            TempNode.variance = variance;
                            TempNode.intensity = intensity;
                            TempNode.S.Radius = radius;
                        }

                        TempNode.left = LBVHNode.left;
                        SGTree[WriteIndex] = TempNode;
                    }
                }
            }
            CommonFunctions.DeepClean(ref nodes);
#endif

        }

        public void ClearAll() {
            // LightTrisArray.Dispose();
            // nodes2Array.Dispose();
            // // nodesArray.Dispose();
            // SAHArray.Dispose();
            // indices_going_left_array.Dispose();
            // tempArray.Dispose();
            // DimensionedIndicesArray.Dispose();
            CommonFunctions.DeepClean(ref FinalIndices);
            CommonFunctions.DeepClean(ref nodes);
#if !DontUseSGTree
            CommonFunctions.DeepClean(ref SGTree);
#endif
        }
    }
}