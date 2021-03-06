﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.BPerf.StackViewer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Diagnostics.Tracing.StackSources;
    using Microsoft.BPerf.ModuleInformation.Abstractions;
    using Microsoft.BPerf.StackAggregation;
    using Microsoft.BPerf.StackInformation.Etw;
    using Microsoft.BPerf.SymbolicInformation.ProgramDatabase;
    using Microsoft.BPerf.SymbolServer.Interfaces;
    using Microsoft.Diagnostics.Tracing.Stacks;
    using Newtonsoft.Json.Linq;

    public sealed class CallTreeData : ICallTreeData
    {
        private readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        private readonly Dictionary<string, CallTreeNodeBase> nodeNameCache = new Dictionary<string, CallTreeNodeBase>();

        private readonly Dictionary<CallTreeNodeBase, TreeNode> callerTreeCache = new Dictionary<CallTreeNodeBase, TreeNode>();

        private readonly Dictionary<CallTreeNodeBase, TreeNode> calleeTreeCache = new Dictionary<CallTreeNodeBase, TreeNode>();

        private readonly object lockobj = new object();

        private readonly ISymbolServerArtifactRetriever symbolServerArtifactRetriever;

        private readonly ISourceServerAuthorizationInformationProvider sourceServerInformationProvider;

        private readonly EtwDeserializer deserializer;

        private readonly StackViewerModel model;

        private int initialized;

        private CallTree callTree;

        public CallTreeData(ISymbolServerArtifactRetriever symbolServerArtifactRetriever, ISourceServerAuthorizationInformationProvider sourceServerInformationProvider, EtwDeserializer deserializer, StackViewerModel model)
        {
            this.symbolServerArtifactRetriever = symbolServerArtifactRetriever;
            this.sourceServerInformationProvider = sourceServerInformationProvider;
            this.deserializer = deserializer;
            this.model = model;
        }

        public async ValueTask<TreeNode> GetNode(string name)
        {
            await this.EnsureInitialized();

            lock (this.lockobj)
            {
                if (this.nodeNameCache.ContainsKey(name))
                {
                    CallTreeDataEventSource.Log.NodeCacheHit(name);
                    return new TreeNode(this.nodeNameCache[name]);
                }
                else
                {
                    foreach (var node in this.callTree.ByID)
                    {
                        if (node.Name == name)
                        {
                            this.nodeNameCache.Add(name, node);
                            CallTreeDataEventSource.Log.NodeCacheMisss(name);
                            return new TreeNode(node);
                        }
                    }

                    CallTreeDataEventSource.Log.NodeCacheNotFound(name);
                    return null;
                }
            }
        }

        public async ValueTask<TreeNode> GetCallerTreeNode(string name, string path = "")
        {
            if (name == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(name));
            }

            var node = await this.GetNode(name);

            lock (this.lockobj)
            {
                CallTreeNodeBase backingNode = node.BackingNode;
                TreeNode callerTreeNode;

                if (this.callerTreeCache.ContainsKey(backingNode))
                {
                    callerTreeNode = this.callerTreeCache[backingNode];
                }
                else
                {
                    callerTreeNode = new TreeNode(AggregateCallTreeNode.CallerTree(backingNode));
                    this.callerTreeCache.Add(backingNode, callerTreeNode);
                }

                if (string.IsNullOrEmpty(path))
                {
                    return callerTreeNode;
                }

                var pathArr = path.Split('/');
                var pathNodeRoot = callerTreeNode.Children[int.Parse(pathArr[0])];

                for (int i = 1; i < pathArr.Length; ++i)
                {
                    pathNodeRoot = pathNodeRoot.Children[int.Parse(pathArr[i])];
                }

                return pathNodeRoot;
            }
        }

        public async ValueTask<TreeNode> GetCalleeTreeNode(string name, string path = "")
        {
            if (name == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(name));
            }

            var node = await this.GetNode(name);

            lock (this.lockobj)
            {
                CallTreeNodeBase backingNode = node.BackingNode;
                TreeNode calleeTreeNode;

                if (this.calleeTreeCache.ContainsKey(backingNode))
                {
                    calleeTreeNode = this.calleeTreeCache[backingNode];
                }
                else
                {
                    calleeTreeNode = new TreeNode(AggregateCallTreeNode.CalleeTree(backingNode));
                    this.calleeTreeCache.Add(backingNode, calleeTreeNode);
                }

                if (string.IsNullOrEmpty(path))
                {
                    return calleeTreeNode;
                }

                var pathArr = path.Split('/');
                var pathNodeRoot = calleeTreeNode.Children[int.Parse(pathArr[0])];

                for (int i = 1; i < pathArr.Length; ++i)
                {
                    pathNodeRoot = pathNodeRoot.Children[int.Parse(pathArr[i])];
                }

                return pathNodeRoot;
            }
        }

        public async ValueTask<TreeNode[]> GetCallerTree(string name)
        {
            var node = await this.GetCallerTreeNode(name);
            return node.Children;
        }

        public async ValueTask<TreeNode[]> GetCallerTree(string name, string path)
        {
            var node = await this.GetCallerTreeNode(name, path);
            return node.Children;
        }

        public async ValueTask<TreeNode[]> GetCalleeTree(string name)
        {
            var node = await this.GetCalleeTreeNode(name);
            return node.Children;
        }

        public async ValueTask<TreeNode[]> GetCalleeTree(string name, string path)
        {
            var node = await this.GetCalleeTreeNode(name, path);
            return node.Children;
        }

        public async ValueTask<List<TreeNode>> GetSummaryTree(int numNodes)
        {
            await this.EnsureInitialized();

            var nodes = this.callTree.ByIDSortedExclusiveMetric().Take(numNodes);

            var summaryNodes = new List<TreeNode>();
            foreach (CallTreeNodeBase node in nodes)
            {
                summaryNodes.Add(new TreeNode(node));
            }

            return summaryNodes;
        }

        public async ValueTask<SourceInformation> Source(TreeNode node)
        {
            var index = this.GetSourceLocation(node.BackingNode, node.Name, out Dictionary<StackSourceFrameIndex, float> retVal);
            var generic = this.callTree.StackSource.BaseStackSource as GenericStackSource;

            var sourceLocation = await generic.GetSourceLocation(index);

            // TODO: needs cleanup
            if (sourceLocation != null)
            {
                var buildTimePath = sourceLocation.SourceFile.BuildTimeFilePath;
                var srcSrvString = sourceLocation.SourceFile.SrcSrvString;

                // TODO: src srv stream needs more support, also talk to VS folks and see how they do SourceLink
                if (srcSrvString != null)
                {
                    var doc = JObject.Parse(srcSrvString)["documents"].ToObject<Dictionary<string, string>>().First();
                    string urlPath = doc.Value.Replace("*", buildTimePath.Replace(doc.Key.Replace("*", string.Empty), string.Empty));

                    var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    {
                        BaseAddress = new Uri(urlPath)
                    };

                    var authorizationHeader = this.sourceServerInformationProvider.GetAuthorizationHeaderValue(urlPath);
                    if (!string.IsNullOrEmpty(authorizationHeader))
                    {
                        client.DefaultRequestHeaders.Add("Authorization", authorizationHeader);
                    }

                    var result = await client.GetStringAsync(urlPath);

                    var lines = result.Split('\n');

                    var list = new List<LineInformation>();

                    int i = 1;
                    foreach (var line in lines)
                    {
                        var li = new LineInformation
                        {
                            Line = line,
                            LineNumber = i++
                        };

                        list.Add(li);
                    }

                    var si = new SourceInformation
                    {
                        BuildTimeFilePath = buildTimePath,
                        Lines = list,
                        Summary = new List<LineInformation> { new LineInformation { LineNumber = sourceLocation.LineNumber, Metric = retVal[index] } }
                    };

                    return si;
                }
            }

            return null; // TODO: need to implement the local case i.e. this is the build machine
        }

        private async Task EnsureInitialized()
        {
            if (Interlocked.CompareExchange(ref this.initialized, 1, comparand: -1) == 0)
            {
                await this.Initialize();
            }
        }

        private async Task Initialize()
        {
            await this.semaphoreSlim.WaitAsync();

            try
            {
                if (this.initialized == 1)
                {
                    return;
                }

                var pid = uint.Parse(this.model.Pid);
                var symbolProvider = new TracePdbSymbolReaderProvider(this.symbolServerArtifactRetriever);

                if (this.deserializer.ImageLoadMap.TryGetValue(pid, out var images))
                {
                    int total = 0;
                    int count = 0;
                    foreach (var image in images)
                    {
                        count++;
                        total += image.InstructionPointers.Count;
                    }

                    var pdbLookupImageList = new List<ImageInfo>(count);
                    var ftotal = (float)total;
                    foreach (var image in images)
                    {
                        if ((image.InstructionPointers.Count / ftotal) * 100 >= 1.0)
                        {
                            pdbLookupImageList.Add(image);
                        }
                    }

                    foreach (var image in pdbLookupImageList)
                    {
                        if (this.deserializer.ImageToDebugInfoMap.TryGetValue(new ProcessImageId(pid, image.Begin), out var dbgId))
                        {
                            await symbolProvider.GetSymbolReader(image.FilePath, dbgId.Signature, dbgId.Age, dbgId.Filename);
                        }
                    }
                }

                var filterParams = new FilterParams
                {
                    StartTimeRelativeMSec = this.model.Start,
                    EndTimeRelativeMSec = this.model.End,
                    ExcludeRegExs = this.model.ExcPats,
                    IncludeRegExs = this.model.IncPats,
                    FoldRegExs = this.model.FoldPats,
                    GroupRegExs = this.model.GroupPats,
                    MinInclusiveTimePercent = this.model.FoldPct,
                    Name = "NoName"
                };

                var stackType = int.Parse(this.model.StackType);
                if (!this.deserializer.EventStacks.TryGetValue(stackType, out var stackEventType))
                {
                    throw new ArgumentException();
                }

                var instructionPointerDecoder = new InstructionPointerToSymbolicNameProvider(this.deserializer, symbolProvider);
                var stackSource = new GenericStackSource(
                    this.deserializer,
                    instructionPointerDecoder,
                    callback =>
                    {
                        var sampleSource = this.deserializer;
                        var samples = stackEventType.SampleIndices;
                        foreach (var s in samples)
                        {
                            var sample = sampleSource.Samples[s];
                            if (sample.Scenario == pid)
                            {
                                callback(sampleSource.Samples[s]);
                            }
                        }
                    });

                var ss = new FilterStackSource(filterParams, stackSource, ScalingPolicyKind.TimeMetric);

                double startTimeRelativeMsec = double.TryParse(filterParams.StartTimeRelativeMSec, out startTimeRelativeMsec) ? Math.Max(startTimeRelativeMsec, 0.0) : 0.0;
                double endTimeRelativeMsec = double.TryParse(filterParams.EndTimeRelativeMSec, out endTimeRelativeMsec) ? Math.Min(endTimeRelativeMsec, ss.SampleTimeRelativeMSecLimit) : ss.SampleTimeRelativeMSecLimit;

                this.callTree = new CallTree(ScalingPolicyKind.TimeMetric);
                this.callTree.TimeHistogramController = new TimeHistogramController(this.callTree, startTimeRelativeMsec, endTimeRelativeMsec);
                this.callTree.StackSource = ss;

                if (float.TryParse(filterParams.MinInclusiveTimePercent, out float minIncusiveTimePercent) && minIncusiveTimePercent > 0)
                {
                    this.callTree.FoldNodesUnder(minIncusiveTimePercent * this.callTree.Root.InclusiveMetric / 100, true);
                }

                this.initialized = 1;
            }
            finally
            {
                this.semaphoreSlim.Release();
            }
        }

        private StackSourceFrameIndex GetSourceLocation(CallTreeNodeBase node, string cellText, out Dictionary<StackSourceFrameIndex, float> retVal)
        {
            var m = Regex.Match(cellText, "<<(.*!.*)>>");

            if (m.Success)
            {
                cellText = m.Groups[1].Value;
            }

            var frameIndexCounts = new Dictionary<StackSourceFrameIndex, float>();
            node.GetSamples(false, sampleIdx =>
            {
                var matchingFrameIndex = StackSourceFrameIndex.Invalid;
                var sample = this.callTree.StackSource.GetSampleByIndex(sampleIdx);
                var callStackIdx = sample.StackIndex;

                while (callStackIdx != StackSourceCallStackIndex.Invalid)
                {
                    var frameIndex = this.callTree.StackSource.GetFrameIndex(callStackIdx);
                    var frameName = this.callTree.StackSource.GetFrameName(frameIndex, false);

                    if (frameName == cellText)
                    {
                        matchingFrameIndex = frameIndex;
                    }

                    callStackIdx = this.callTree.StackSource.GetCallerIndex(callStackIdx);
                }

                if (matchingFrameIndex != StackSourceFrameIndex.Invalid)
                {
                    frameIndexCounts.TryGetValue(matchingFrameIndex, out float count);
                    frameIndexCounts[matchingFrameIndex] = count + sample.Metric;
                }

                return true;
            });

            var maxFrameIdx = StackSourceFrameIndex.Invalid;
            float maxFrameIdxCount = -1;
            foreach (var keyValue in frameIndexCounts)
            {
                if (keyValue.Value >= maxFrameIdxCount)
                {
                    maxFrameIdxCount = keyValue.Value;
                    maxFrameIdx = keyValue.Key;
                }
            }

            retVal = frameIndexCounts;

            return maxFrameIdx;
        }
    }
}
