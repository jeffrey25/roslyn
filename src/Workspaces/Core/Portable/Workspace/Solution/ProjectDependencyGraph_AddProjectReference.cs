﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    partial class ProjectDependencyGraph
    {
        internal ProjectDependencyGraph WithAdditionalProjectReferences(ProjectId projectId, IReadOnlyList<ProjectId> referencedProjectIds)
        {
            Contract.ThrowIfFalse(_projectIds.Contains(projectId));

            if (referencedProjectIds.Count == 0)
            {
                return this;
            }

            var newReferencesMap = ComputeNewReferencesMapForAdditionalProjectReferences(_referencesMap, projectId, referencedProjectIds);

            var newReverseReferencesMap =
                _lazyReverseReferencesMap != null
                    ? ComputeNewReverseReferencesMapForAdditionalProjectReferences(_lazyReverseReferencesMap, projectId, referencedProjectIds)
                    : null;

            var newTransitiveReferencesMap = ComputeNewTransitiveReferencesMapForAdditionalProjectReferences(_transitiveReferencesMap, projectId, referencedProjectIds);

            var newReverseTransitiveReferencesMap = ComputeNewReverseTransitiveReferencesMapForAdditionalProjectReferences(_reverseTransitiveReferencesMap, projectId, referencedProjectIds);

            // Note: rather than updating our dependency sets and topologically sorted data, we'll throw that away since incremental update is
            // tricky, and those are rarely used. If somebody needs them, it'll be lazily computed.
            return new ProjectDependencyGraph(
                _projectIds,
                referencesMap: newReferencesMap,
                reverseReferencesMap: newReverseReferencesMap,
                transitiveReferencesMap: newTransitiveReferencesMap,
                reverseTransitiveReferencesMap: newReverseTransitiveReferencesMap,
                topologicallySortedProjects: default,
                dependencySets: default);
        }

        /// <summary>
        /// Computes a new <see cref="_referencesMap"/> for the addition of additional project references.
        /// </summary>
        private static ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> ComputeNewReferencesMapForAdditionalProjectReferences(
            ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> existingReferencesMap,
            ProjectId projectId,
            IReadOnlyList<ProjectId> referencedProjectIds)
        {
            if (existingReferencesMap.TryGetValue(projectId, out var existingReferences))
            {
                return existingReferencesMap.SetItem(projectId, existingReferences.Union(referencedProjectIds));
            }
            else
            {
                return existingReferencesMap.SetItem(projectId, referencedProjectIds.ToImmutableHashSet());
            }
        }

        /// <summary>
        /// Computes a new <see cref="_lazyReverseReferencesMap"/> for the addition of additional project references.
        /// Must be called on a non-null map.
        /// </summary>
        private static ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> ComputeNewReverseReferencesMapForAdditionalProjectReferences(
            ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> existingReverseReferencesMap,
            ProjectId projectId,
            IReadOnlyList<ProjectId> referencedProjectIds)
        {
            var builder = existingReverseReferencesMap.ToBuilder();

            foreach (var referencedProject in referencedProjectIds)
            {
                if (builder.TryGetValue(referencedProject, out var reverseReferences))
                {
                    builder[referencedProject] = reverseReferences.Add(projectId);
                }
                else
                {
                    builder[referencedProject] = ImmutableHashSet.Create(projectId);
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Computes a new <see cref="_transitiveReferencesMap"/> for the addition of additional project references. 
        /// </summary>
        private static ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> ComputeNewTransitiveReferencesMapForAdditionalProjectReferences(
            ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> existingTransitiveReferencesMap,
            ProjectId projectId,
            IReadOnlyList<ProjectId> referencedProjectIds)
        {
            // To update our forward transitive map, we need to add referencedProjectIds (and their transitive dependencies) to the transitive references
            // of projects. First, let's just compute the new set of transitive references. It's possible while doing so we'll discover that we don't
            // know the transitive project references for one of our new references. In that case, we'll use null as a sentinel to mean "we don't know" and
            // we propagate the not-knowingness. But let's not worry about that yet. First, let's just get the new transitive reference set.
            HashSet<ProjectId>? newTransitiveReferences = new HashSet<ProjectId>(referencedProjectIds);

            foreach (var referencedProjectId in referencedProjectIds)
            {
                if (existingTransitiveReferencesMap.TryGetValue(referencedProjectId, out var additionalTransitiveReferences))
                {
                    newTransitiveReferences.UnionWith(additionalTransitiveReferences);
                }
                else
                {
                    newTransitiveReferences = null;
                    break;
                }
            }

            // We'll now loop through each entry in our existing cache and compute updates. We'll accumulate them into this builder.
            var builder = existingTransitiveReferencesMap.ToBuilder();

            foreach (var projectIdToUpdate in existingTransitiveReferencesMap.Keys)
            {
                existingTransitiveReferencesMap.TryGetValue(projectIdToUpdate, out var existingTransitiveReferences);

                // The projects who need to have their caches updated are projectIdToUpdate (since we're obviously updating it!)
                // and also anything that depended on it.
                if (projectIdToUpdate == projectId || existingTransitiveReferences?.Contains(projectId) == true)
                {
                    // This needs an update. If we know what to include in, we'll union it with the existing ones. Otherwise, we don't know
                    // and we'll remove any data from the cache.
                    if (newTransitiveReferences != null && existingTransitiveReferences != null)
                    {
                        builder[projectIdToUpdate] = existingTransitiveReferences.Union(newTransitiveReferences);
                    }
                    else
                    {
                        // Either we don't know the full set of the new references being added, or don't know the existing set projectIdToUpdate.
                        // In this case, just remove it
                        builder.Remove(projectIdToUpdate);
                    }
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Computes a new <see cref="_reverseTransitiveReferencesMap"/> for the addition of new projects.
        /// </summary>
        private static ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> ComputeNewReverseTransitiveReferencesMapForAdditionalProjectReferences(
            ImmutableDictionary<ProjectId, ImmutableHashSet<ProjectId>> existingReverseTransitiveReferencesMap,
            ProjectId projectId,
            IReadOnlyList<ProjectId> referencedProjectIds)
        {
            // To update the reverse transitive map, we need to add the existing reverse transitive references of projectId to any of referencedProjectIds,
            // and anything else with a reverse dependency on them. If we don't already know our reverse transitive references, then we'll have to instead remove
            // the cache entries instead of update them. We'll fetch this from the map, and use "null" to indicate the "we don't know and should remove the cache entry"
            // instead
            existingReverseTransitiveReferencesMap.TryGetValue(projectId, out var newReverseTranstiveReferences);

            if (newReverseTranstiveReferences != null)
            {
                newReverseTranstiveReferences = newReverseTranstiveReferences.Add(projectId);
            }

            // We'll now loop through each entry in our existing cache and compute updates. We'll accumulate them into this builder.
            var builder = existingReverseTransitiveReferencesMap.ToBuilder();

            foreach (var projectIdToUpdate in existingReverseTransitiveReferencesMap.Keys)
            {
                existingReverseTransitiveReferencesMap.TryGetValue(projectIdToUpdate, out var existingReverseTransitiveReferences);

                // The projects who need to have their caches updated are projectIdToUpdate (since we're obviously updating it!)
                // and also anything that depended on us.
                if (referencedProjectIds.Contains(projectIdToUpdate) || existingReverseTransitiveReferences?.Overlaps(referencedProjectIds) == true)
                {
                    // This needs an update. If we know what to include in, we'll union it with the existing ones. Otherwise, we don't know
                    // and we'll remove any data from the cache.
                    if (newReverseTranstiveReferences != null && existingReverseTransitiveReferences != null)
                    {
                        builder[projectIdToUpdate] = existingReverseTransitiveReferences.Union(newReverseTranstiveReferences);
                    }
                    else
                    {
                        // Either we don't know the full set of the new references being added, or don't know the existing set projectIdToUpdate.
                        // In this case, just remove it
                        builder.Remove(projectIdToUpdate);
                    }
                }
            }

            return builder.ToImmutable();
        }
    }
}
