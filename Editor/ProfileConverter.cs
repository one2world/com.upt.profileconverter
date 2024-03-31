using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Profiling
{
    using Debug = UnityEngine.Debug;

    static class ProfileConverter
    {
        static void Run(string filename, ColumnType columnType)
        {
            void OnProfileLoaded()
            {
                ProfilerDriver.profileLoaded -= OnProfileLoaded;

                var firstFrame = 1 + ProfilerDriver.firstFrameIndex;
                var lastFrame = 1 + ProfilerDriver.lastFrameIndex;

                Debug.Log($"Load Success :{(lastFrame - firstFrame)} Frames, wait seconds...");
                ProfilerFrameDataIterator frameData = new ProfilerFrameDataIterator();
                int firstFrameIndex = firstFrame;
                int lastFrameIndex = lastFrame - 1;

                var frameIndexOffset = firstFrameIndex;

                HashSet<string> threadNames = new HashSet<string>();
                string GetThreadNameWithGroup(string threadName, string groupName)
                {
                    if (string.IsNullOrEmpty(groupName))
                        return threadName;

                    return string.Format("{0}.{1}", groupName, threadName);
                }

                Stack<List<int>> s_pool = new Stack<List<int>>();
                List<float> s_floatList = new List<float>();
                List<int> GetFromPool()
                {
                    List<int> r = null;
                    if (s_pool.Count > 0)
                    {
                        r = s_pool.Pop();
                    }
                    else
                    {
                        r = new List<int>();
                    }
                    return r;
                }

                void ReleaseToPool(List<int> list)
                {
                    if (list == null)
                        return;
                    list.Clear();
                    s_pool.Push(list);
                }

                bool firstEvent = true;
                HashSet<(string, ulong)> ThreadNameHashSet = new HashSet<(string, ulong)>();
                Dictionary<string, double> ThreadTimeHashDic = new Dictionary<string, double>();
                Stack<string> Names = new Stack<string>(256);
                using (StreamWriter perfettoWriter = new StreamWriter($"{filename}_{columnType.ToString()}.perfetto.json", false, Encoding.ASCII))
                {
                    perfettoWriter.WriteLine("{\n\t\"traceEvents\":");

                    using (StreamWriter traceEventWriter = new StreamWriter($"{filename}_{columnType.ToString()}.trace.json", false, Encoding.ASCII))
                    {
                        using (StreamWriter instrumentTextWriter = new StreamWriter($"{filename}_{columnType.ToString()}.instrument.txt", false, Encoding.ASCII))
                        {
                            perfettoWriter.WriteLine("[");
                            traceEventWriter.WriteLine("[");

                            for (int frameIndex = firstFrameIndex; frameIndex <= lastFrameIndex; ++frameIndex)
                            {
                                int threadCount = frameData.GetThreadCount(frameIndex);
                                frameData.SetRoot(frameIndex, 0);
                                var msFrame = frameData.frameTimeMS;

                                for (int threadIndex = 0; threadIndex < threadCount; ++threadIndex)
                                {
                                    frameData.SetRoot(frameIndex, threadIndex);
                                    var threadName = frameData.GetThreadName();
                                    if (threadName.Trim() == "")
                                    {
                                        threadName = "UnkonwThread";
                                    }
                                    var groupName = frameData.GetGroupName();
                                    var threadFullName = GetThreadNameWithGroup(threadName, groupName);

                                    var viewMode = HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName | HierarchyFrameDataView.ViewModes.HideEditorOnlySamples;
                                    using (var threadframeData = ProfilerDriver.GetHierarchyFrameDataView(frameIndex, threadIndex, viewMode, (int)columnType, true))
                                    {
                                        ulong tid =  threadframeData.threadId;

                                        if (!ThreadTimeHashDic.TryGetValue(threadFullName, out var threadTime))
                                        {
                                            ThreadTimeHashDic.Add(threadFullName, threadTime = 0);
                                        }
                                        if (ThreadNameHashSet.Add((threadFullName, tid)))
                                        {
                                            if (!firstEvent)
                                            {
                                                perfettoWriter.Write(",");
                                                traceEventWriter.Write(",");
                                            }
                                            firstEvent = false;
                                            perfettoWriter.WriteLine($@"
    {{
        ""args"": {{
            ""name"": ""{threadFullName}""
        }},
        ""cat"": ""__metadata"",
        ""name"": ""thread_name"",
        ""ph"": ""M"",
        ""pid"": 0,
        ""tid"": {tid},
        ""ts"": 0
    }}
");

                                            traceEventWriter.WriteLine($@"
    {{
        ""args"": {{
            ""name"": ""{threadFullName}""
        }},
        ""cat"": ""__metadata"",
        ""name"": ""thread_name"",
        ""ph"": ""M"",
        ""pid"": 0,
        ""tid"": {tid},
        ""ts"": 0
    }}
");
                                        }

                                        float processNode(int id, double startTime, string parentName)
                                        {
                                            string name = threadframeData.GetItemName(id);
                                            if (name.Contains("Editor"))
                                                return 0;
                                            if (name.Trim() == "")
                                            {
                                                name = $"null({threadName})";
                                            }
                                            else
                                            { 
                                                name = name.Replace(' ', '_');
                                            }
                                            threadframeData.GetItemMergedSamplesColumnDataAsFloats(id, (int)columnType, s_floatList);
                                            float value = (float)s_floatList.Sum();
                                            if(value <= 0)
                                            {
                                                return 0;
                                            }

                                            if (columnType == ColumnType.TotalTime)
                                            {
                                                value *= 1000;
                                            }
                                            Names.Push(name);

                                            float selfValue = value;
                                            perfettoWriter.WriteLine($@"
    ,{{
        ""name"": ""{name}"",
        ""cat"": ""{parentName}"",
        ""ts"": {startTime},
        ""dur"": {value},
        ""ph"": ""X"",
        ""pid"": 0,
        ""tid"": {tid},
        ""args"": {{
            ""Frame"": ""{frameIndex}"",
            ""Value"": ""{value}""
        }}
    }}

");

                                            traceEventWriter.WriteLine($@"

    ,{{
        ""name"": ""{name}"",
        ""cat"": ""{parentName}"",
        ""ts"": {startTime},
        ""dur"": {value},
        ""ph"": ""X"",
        ""pid"": 0,
        ""tid"": {tid},
        ""args"": {{
            ""Frame"": ""{frameIndex}"",
            ""Value"": ""{value}"",
        }}
    }}

");

                                            if (threadframeData.HasItemChildren(id))
                                            {
                                                var childrenIds = GetFromPool();
                                                threadframeData.GetItemChildren(id, childrenIds);
                                                for (int childIndex = 0; childIndex < childrenIds.Count; childIndex++)
                                                {
                                                    var childValue = processNode(childrenIds[childIndex], startTime, name);
                                                    startTime += childValue;
                                                    selfValue -= childValue;
                                                }
                                                ReleaseToPool(childrenIds);
                                                if (selfValue >0)
                                                {
                                                    instrumentTextWriter.WriteLine($@"{string.Join(";", Names.Reverse())} {selfValue}");
                                                }
                                            }
                                            else
                                            {
                                                if (value > 0)
                                                {
                                                    instrumentTextWriter.WriteLine($@"{string.Join(";", Names.Reverse())} {value}");
                                                }
                                            }

                                            Names.Pop();
                                            return value;
                                        }

                                        var threadDur = processNode(threadframeData.GetRootItemID(), threadTime, "Root");
                                        ThreadTimeHashDic[threadFullName] = threadTime + threadDur;
                                    }

                                }
                                if (frameIndex > firstFrameIndex + 6)
                                    break;
                            }
                            frameData.Dispose();

                            Debug.Log($"<color=#44ff2e>all Success</color>, out file : {(instrumentTextWriter.BaseStream as FileStream).Name}");
                        }
                        traceEventWriter.Write("]");
                        perfettoWriter.Write("]");
                        Debug.Log($"<color=#44ff2e>all Success</color>, out file : {(traceEventWriter.BaseStream as FileStream).Name}");
                    }
                    perfettoWriter.WriteLine("}");
                    Debug.Log($"<color=#44ff2e>all Success</color>, out file : {(perfettoWriter.BaseStream as FileStream).Name}");
                }
            };
            ProfilerDriver.profileLoaded += OnProfileLoaded;
            if (!ProfilerDriver.LoadProfile(filename, false))
            {
                ProfilerDriver.profileLoaded -= OnProfileLoaded;
                Debug.LogError("Failed to load profile file");
            }
        }

        enum ColumnType
        {
            Calls = 3, //columnCalls
            GcMemory = 4,// columnGcMemory
            TotalTime = 5, //columnTotalTime
        }

#if UNITY_2018_1_OR_NEWER
        [MenuItem("Window/Analysis/Profile Data Convert")]
#else
        [MenuItem("Window/Profile Data Convert")]
#endif
        public static void MenuItem()
        {
            EditorApplication.ExecuteMenuItem("Window/Analysis/Profiler");
            ProfilerDriver.enabled = false;
            string filePath = UnityEditor.EditorUtility.OpenFilePanelWithFilters("选择文件", "", new string[] { "raw", "data" });
            Run(filePath, ColumnType.TotalTime);
            //Run(filePath, ColumnType.GcMemory);
        }
    }
}