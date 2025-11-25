using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Minge2025Summer.Scripts.RenderingScript
{
    public class FlyController : MonoBehaviour
    {
        #region Parameters
        [Header("ハエの描画関連")] 
        [SerializeField] private Mesh flyMesh;
        [SerializeField] private Material flyMaterial;
        
        public ComputeShader computeShader;
        public int flyCount = 1000;
        
        [Header("カリング設定")] 
        [SerializeField, Tooltip("フラスタムベースの簡易オクルージョンカリングを有効化")] private bool enableFrustumCulling = true;
        [SerializeField, Tooltip("フラスタムに少し余裕を持たせる半径(Boid位置)補正")] private float frustumMargin = 0.0f;

        [Header("群れの挙動パラメータ")]
        public float separationWeight = 1.0f;
        public float alignmentWeight = 1.0f;
        public float cohesionWeight = 1.0f;
        public float perceptionRadius = 2.0f;

        [Header("追加のパラメータ")] 
        [SerializeField, Tooltip("目標地点")] private Transform target;
        [SerializeField, Tooltip("目標地点への追従度合い")] private float targetWeight = 1.0f;
        [SerializeField, Tooltip("境界のサイズ")] private Vector3 boundsSize = new Vector3(20, 20, 20);
        [SerializeField, Tooltip("境界への回避度合い")] private float boundsWeight = 2.0f;
        [SerializeField, Tooltip("ノイズの強さ")] private float noiseStrength = 0.5f;

        [Header("障害物回避パラメータ")] 
        [SerializeField, Tooltip("障害物オブジェクト")] private Transform[] obstacles;
        [SerializeField, Tooltip("障害物回避の強さ")] private float obstacleAvoidanceWeight = 3.0f;
        [SerializeField, Tooltip("障害物回避の感知距離")] private float obstacleAvoidanceRadius = 1.5f;
        #endregion
        
        #region InternalVariables
        private ComputeBuffer flyDataBuffer;
        private ComputeBuffer obstacleDataBuffer;
        private ComputeBuffer argsBuffer;
        private ComputeBuffer visibleBoidBuffer; // AppendStructuredBuffer用
        private ComputeBuffer countBuffer;       // 1uintのRawカウンタ読み出し
        
        private GameObject[] flies;
        private uint[] args = new uint[5] {0, 0, 0, 0, 0};
        private int lastObstacleCount = -1;

        private Material runtimeMaterial;

        private int boidDataBufferID;
        #endregion

        #region Structures
        [StructLayout(LayoutKind.Sequential)]
        struct FlyData
        {
            public Vector3 position;
            public Vector3 velocity;
            public Matrix4x4 mat;
            public int state;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ObstacleData
        {
            public Vector3 position;
            public float radius;
        }
        #endregion

        private void Start()
        {
            boidDataBufferID = Shader.PropertyToID("boidDataBuffer");
            runtimeMaterial = new Material(flyMaterial) { enableInstancing = true };

            InitializeFlies();
            InitializeObstacles();

            // 間接描画用のバッファを設定
            argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            args[0] = (flyMesh != null) ? flyMesh.GetIndexCount(0) : 0;
            args[1] = (uint)flyCount; // インスタンス数
            args[2] = (flyMesh != null) ? flyMesh.GetIndexStart(0) : 0;
            args[3] = (flyMesh != null) ? flyMesh.GetBaseVertex(0) : 0;
            args[4] = 0;
            argsBuffer.SetData(args);

            visibleBoidBuffer = new ComputeBuffer(flyCount, Marshal.SizeOf(typeof(FlyData)), ComputeBufferType.Append);
            countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
        }

        private void Update()
        {
            EnsureObstacleBuffer();

            // ComputeShaderの実行
            computeShader.SetFloat("deltaTime", Time.deltaTime);
            computeShader.SetInt("boidCount", flyCount);
            computeShader.SetFloat("perceptionRadius", perceptionRadius);
            computeShader.SetFloat("separationWeight", separationWeight);
            computeShader.SetFloat("alignmentWeight", alignmentWeight);
            computeShader.SetFloat("cohesionWeight", cohesionWeight);

            Vector3 targetPos = target ? target.position : transform.position;
            computeShader.SetVector("targetPosition", targetPos);
            computeShader.SetFloat("targetWeight", targetWeight);
            computeShader.SetVector("boundsSize", boundsSize);
            computeShader.SetVector("boundsCenter", transform.position);
            computeShader.SetFloat("boundsWeight", boundsWeight);
            computeShader.SetFloat("noiseStrength", noiseStrength);
            computeShader.SetInt("obstacleCount", obstacles?.Length ?? 0);
            computeShader.SetFloat("obstacleAvoidanceWeight", obstacleAvoidanceWeight);
            computeShader.SetFloat("obstacleAvoidanceRadius", obstacleAvoidanceRadius);

            int kernelIndex = computeShader.FindKernel("CSMain");
            computeShader.SetBuffer(kernelIndex, "boidDataBuffer", flyDataBuffer);
            computeShader.SetBuffer(kernelIndex, "obstacleDataBuffer", obstacleDataBuffer);

            int threadGroups = Mathf.CeilToInt(flyCount / 64.0f);
            computeShader.Dispatch(kernelIndex, threadGroups, 1, 1);

            // マテリアルにバッファをバインド - これが重要!
            runtimeMaterial.SetBuffer(boidDataBufferID, flyDataBuffer);

            // 描画
            Bounds drawBounds = new Bounds(transform.position, boundsSize * 2f);
            
            if (enableFrustumCulling)
            {
                // カリング処理
                int cullKernelIndex = computeShader.FindKernel("CSCull");
                Camera cam = Camera.main;
                if (cam == null) return;

                Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
                computeShader.SetMatrix("viewProjMatrix", vp);
                computeShader.SetFloat("frustumMargin", frustumMargin);

                visibleBoidBuffer.SetCounterValue(0);
                computeShader.SetBuffer(cullKernelIndex, "boidDataBuffer", flyDataBuffer);
                computeShader.SetBuffer(cullKernelIndex, "visibleBoidBuffer", visibleBoidBuffer);
                computeShader.Dispatch(cullKernelIndex, threadGroups, 1, 1);

                ComputeBuffer.CopyCount(visibleBoidBuffer, countBuffer, 0);

                uint[] countData = new uint[1];
                countBuffer.GetData(countData);
                args[1] = countData[0];
                argsBuffer.SetData(args);

                // カリング後のバッファをマテリアルに再バインド
                runtimeMaterial.SetBuffer(boidDataBufferID, visibleBoidBuffer);
                Graphics.DrawMeshInstancedIndirect(flyMesh, 0, runtimeMaterial, drawBounds, argsBuffer);
            }
            else
            {
                args[1] = (uint)flyCount;
                argsBuffer.SetData(args);
                Graphics.DrawMeshInstancedIndirect(flyMesh, 0, runtimeMaterial, drawBounds, argsBuffer);
            }
        }

        /// <summary>
        /// ハエの初期化
        /// </summary>
        private void InitializeFlies()
        {
            List<FlyData> initialFlyData = new List<FlyData>(flyCount);
            for (int i = 0; i < flyCount; i++)
            {
                Vector3 pos = transform.position + Random.insideUnitSphere * 5.0f;
                Vector3 vel = Random.insideUnitSphere * 2.0f;

                FlyData flyData = new FlyData()
                {
                    position = pos,
                    velocity = vel,
                    mat = Matrix4x4.TRS(pos, Quaternion.LookRotation(vel), Vector3.one * 0.1f),
                    state = 0
                };
                
                initialFlyData.Add(flyData);
            }

            flyDataBuffer = new ComputeBuffer(flyCount, Marshal.SizeOf(typeof(FlyData)));
            flyDataBuffer.SetData(initialFlyData);
        }

        /// <summary>
        /// 障害物データの初期化
        /// </summary>
        private void InitializeObstacles()
        {
            obstacleDataBuffer?.Release();
            obstacleDataBuffer = null;
            
            int count = (obstacles != null && obstacles.Length > 0) ? obstacles.Length : 1;
            var data = new ObstacleData[count];
            if (obstacles != null && obstacles.Length > 0)
            {
                for (int i = 0; i < obstacles.Length; i++)
                {
                    data[i] = new ObstacleData()
                    {
                        position = obstacles[i].position,
                        radius = obstacles[i].localScale.x * 0.5f
                    };
                }
            }
            else
            {
                data[0] = new ObstacleData { position = Vector3.zero, radius = 0.0f };
            }
            
            obstacleDataBuffer = new ComputeBuffer(count, Marshal.SizeOf(typeof(ObstacleData)));
            obstacleDataBuffer.SetData(data);
            lastObstacleCount = obstacles?.Length ?? 0;
        }

        /// <summary>
        /// 障害物バッファの確認と更新
        /// 障害物リストが変化していたらバッファを再初期化
        /// </summary>
        private void EnsureObstacleBuffer()
        {
            int current = obstacles?.Length ?? 0;
            if (obstacleDataBuffer == null || current != lastObstacleCount)
                InitializeObstacles();
        }

        private void OnDestroy()
        {
            if (flyDataBuffer != null)
                flyDataBuffer.Release();
            
            if (obstacleDataBuffer != null)
                obstacleDataBuffer.Release();
            
            if (argsBuffer != null)
                argsBuffer.Release();

            if (visibleBoidBuffer != null)
                visibleBoidBuffer.Release();
            
            if (countBuffer != null)
                countBuffer.Release();
        }

        /// <summary>
        /// 境界線を表示
        /// </summary>
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(transform.position, boundsSize);
        }
    }
}