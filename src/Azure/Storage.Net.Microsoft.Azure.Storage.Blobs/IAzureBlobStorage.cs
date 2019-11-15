﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Storage.Net.Blobs;

namespace Storage.Net.Microsoft.Azure.Storage.Blobs
{
   public interface IAzureBlobStorage : IBlobStorage
   {
      /// <summary>
      /// Acquires a lease
      /// </summary>
      /// <param name="fullPath">Path to container or blob</param>
      /// <param name="maxLeaseTime">When set, leases for a maximum period of time, otherwise lease is set for infinite time.</param>
      /// <param name="proposedLeaseId">When specified, will use as lease ID.</param>
      /// <param name="waitForRelease">When true, the call will wait for the lease to be released.</param>
      /// <param name="cancellationToken"></param>
      /// <returns></returns>
      Task<AzureStorageLease> AcquireLeaseAsync(
         string fullPath,
         TimeSpan? maxLeaseTime = null,
         string proposedLeaseId = null,
         bool waitForRelease = false,
         CancellationToken cancellationToken = default);

      /// <summary>
      /// Breaks container or blob lease
      /// </summary>
      /// <param name="fullPath"></param>
      /// <param name="ignoreErrors">When set, all the errors are ignored. Good for initialisation scenarios.</param>
      /// <param name="cancellationToken"></param>
      /// <returns></returns>
      Task BreakLeaseAsync(
         string fullPath,
         bool ignoreErrors = false,
         CancellationToken cancellationToken = default);
   }
}
