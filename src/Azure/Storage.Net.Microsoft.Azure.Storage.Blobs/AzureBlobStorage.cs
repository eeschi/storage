﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Storage.Net.Blobs;

namespace Storage.Net.Microsoft.Azure.Storage.Blobs
{
   //auth scenarios: https://github.com/Azure/azure-sdk-for-net/blob/master/sdk/storage/Azure.Storage.Blobs/samples/Sample02_Auth.cs


   class AzureBlobStorage : IAzureBlobStorage
   {
      private const int BrowserParallelism = 10;
      private readonly BlobServiceClient _client;
      private readonly string _containerName;
      private readonly Dictionary<string, BlobContainerClient> _containerNameToContainerClient =
         new Dictionary<string, BlobContainerClient>();

      public AzureBlobStorage(BlobServiceClient blobServiceClient)
      {
         _client = blobServiceClient;
      }

      #region [ Interface Methods ]

      public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options = null, CancellationToken cancellationToken = default)
      {
         if(options == null)
            options = new ListOptions();

         var result = new List<Blob>();
         var containers = new List<BlobContainerClient>();

         if(StoragePath.IsRootPath(options.FolderPath) && _containerName == null)
         {
            // list all of the containers
            containers.AddRange(await ListContainersAsync(cancellationToken).ConfigureAwait(false));
            result.AddRange(containers.Select(AzConvert.ToBlob));

            if(!options.Recurse)
               return result;
         }
         else
         {
            (BlobContainerClient container, string path) = await GetPartsAsync(options.FolderPath, false).ConfigureAwait(false);
            if(container == null)
               return new List<Blob>();
            options = options.Clone();
            options.FolderPath = path; //scan from subpath now
            containers.Add(container);
         }

         await Task.WhenAll(containers.Select(c => ListAsync(c, result, options, cancellationToken))).ConfigureAwait(false);

         if(options.MaxResults != null)
         {
            result = result.Take(options.MaxResults.Value).ToList();
         }

         return result;
      }





      public async Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPaths(fullPaths);

         await Task.WhenAll(fullPaths.Select(fullPath => DeleteAsync(fullPath, cancellationToken))).ConfigureAwait(false);
      }

      public void Dispose() { }

      public async Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         return await Task.WhenAll(fullPaths.Select(p => ExistsAsync(p, cancellationToken))).ConfigureAwait(false);
      }

      public async Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         return await Task.WhenAll(fullPaths.Select(p => GetBlobAsync(p, cancellationToken))).ConfigureAwait(false);
      }


      public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, false).ConfigureAwait(false);

         BlockBlobClient client = container.GetBlockBlobClient(path);

         try
         {
            //current SDK fails to download 0-sized files
            Response<BlobProperties> p = await client.GetPropertiesAsync().ConfigureAwait(false);
            if(p.Value.ContentLength == 0)
               return new MemoryStream();

            Response<BlobDownloadInfo> response = await client.DownloadAsync(cancellationToken).ConfigureAwait(false);

            return response.Value.Content;
         }
         catch(RequestFailedException ex) when (ex.ErrorCode == "BlobNotFound")
         {
            return null;
         }
      }

      public Task<ITransaction> OpenTransactionAsync() => throw new NotImplementedException();

      public async Task WriteAsync(string fullPath, Stream dataStream,
         bool append = false, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         if(dataStream is null)
            throw new ArgumentNullException(nameof(dataStream));

         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, true).ConfigureAwait(false);

         BlockBlobClient client = container.GetBlockBlobClient(path);

         await client.UploadAsync(
            dataStream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
      }
      public async Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPaths(blobs);

         await Task.WhenAll(blobs.Select(b => SetBlobAsync(b, cancellationToken))).ConfigureAwait(false);
      }

      #endregion

      #region [ IAzureBlobStorage Specific ]

      public async Task<AzureStorageLease> AcquireLeaseAsync(
         string fullPath,
         TimeSpan? maxLeaseTime = null,
         string proposedLeaseId = null,
         bool waitForRelease = false,
         CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         if(maxLeaseTime != null)
         {
            if(maxLeaseTime.Value < TimeSpan.FromSeconds(15) || maxLeaseTime.Value >= TimeSpan.FromMinutes(1))
            {
               throw new ArgumentException(nameof(maxLeaseTime), $"When specifying lease time, make sure it's between 15 seconds and 1 minute, was: {maxLeaseTime.Value}");
            }
         }

         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, true).ConfigureAwait(false);

         //get lease client for container or blob
         BlobLeaseClient leaseClient;
         if(string.IsNullOrEmpty(path))
         {
            leaseClient = container.GetBlobLeaseClient();
         }
         else
         {
            //create a new blob if it doesn't exist
            if(!(await ExistsAsync(fullPath).ConfigureAwait(false)))
            {
               await WriteAsync(fullPath, new MemoryStream(), false, cancellationToken).ConfigureAwait(false);
            }

            BlockBlobClient client = container.GetBlockBlobClient(path);
            leaseClient = client.GetBlobLeaseClient();
         }

         await leaseClient.AcquireAsync(
            maxLeaseTime == null ? TimeSpan.MinValue : maxLeaseTime.Value,
            cancellationToken: cancellationToken).ConfigureAwait(false);

         return new AzureStorageLease(leaseClient);
      }

      public async Task BreakLeaseAsync(string fullPath, bool ignoreErrors = false, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, true).ConfigureAwait(false);

         //get lease client for container or blob
         BlobLeaseClient leaseClient;
         if(string.IsNullOrEmpty(path))
         {
            leaseClient = container.GetBlobLeaseClient();
         }
         else
         {
            BlockBlobClient client = container.GetBlockBlobClient(path);
            leaseClient = client.GetBlobLeaseClient();
         }

         try
         {
            await leaseClient.BreakAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
         }
         catch(RequestFailedException ex) when(ex.ErrorCode == "LeaseNotPresentWithLeaseOperation")
         {
            if(!ignoreErrors)
               throw;
         }
      }

      #endregion

      private async Task SetBlobAsync(Blob blob, CancellationToken cancellationToken)
      {
         if(!(await ExistsAsync(blob, cancellationToken).ConfigureAwait(false)))
            return;

         (BlobContainerClient container, string path) = await GetPartsAsync(blob, false).ConfigureAwait(false);

         if(string.IsNullOrEmpty(path))
         {
            //it's a container!

            await container.SetMetadataAsync(blob.Metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
         }
         else
         {
            BlockBlobClient client = container.GetBlockBlobClient(path);

            await client.SetMetadataAsync(blob.Metadata, cancellationToken: cancellationToken).ConfigureAwait(false);
         }
      }

      private async Task<Blob> GetBlobAsync(string fullPath, CancellationToken cancellationToken)
      {
         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, false).ConfigureAwait(false);

         if(container == null)
            return null;

         if(string.IsNullOrEmpty(path))
         {
            //it's a container

            Response<BlobContainerProperties> attributes = await container.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return AzConvert.ToBlob(container.Name, attributes);
         }

         BlobClient client = container.GetBlobClient(path);

         try
         {
            Response<BlobProperties> properties = await client.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return AzConvert.ToBlob(_containerName, path, properties);
         }
         catch(RequestFailedException ex) when(ex.ErrorCode == "BlobNotFound")
         {
            return null;
         }
      }


      private async Task<IReadOnlyCollection<BlobContainerClient>> ListContainersAsync(CancellationToken cancellationToken)
      {
         var r = new List<BlobContainerClient>();

         await foreach(BlobContainerItem container in _client.GetBlobContainersAsync(BlobContainerTraits.Metadata).ConfigureAwait(false))
         {
            (BlobContainerClient client, _) = await GetPartsAsync(container.Name, false).ConfigureAwait(false);

            if(client != null)
               r.Add(client);
         }

         return r;
      }

      private async Task ListAsync(BlobContainerClient container,
         List<Blob> result,
         ListOptions options,
         CancellationToken cancellationToken)
      {
         using(var browser = new AzureContainerBrowser(container, _containerName == null, BrowserParallelism))
         {
            IReadOnlyCollection<Blob> containerBlobs =
               await browser.ListFolderAsync(options, cancellationToken)
                  .ConfigureAwait(false);

            if(containerBlobs.Count > 0)
            {
               result.AddRange(containerBlobs);
            }
         }
      }

      private async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
      {
         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, false);

         if(StoragePath.IsRootPath(path))
         {
            //deleting the entire container
            await container.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
         }
         else
         {

            BlockBlobClient blob = string.IsNullOrEmpty(path)
               ? null
               : container.GetBlockBlobClient(StoragePath.Normalize(path, false));
            if(blob != null)
            {
               try
               {
                  await blob.DeleteAsync(
                     DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
               }
               catch(RequestFailedException ex) when(ex.ErrorCode == "BlobNotFound")
               {
                  //this might be a folder reference, just try it

                  await foreach(BlobItem recursedFile in
                     container.GetBlobsAsync(prefix: path, cancellationToken: cancellationToken).ConfigureAwait(false))
                  {
                     BlobClient client = container.GetBlobClient(recursedFile.Name);
                     await client.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                  }
               }
            }
         }
      }

      private async Task<bool> ExistsAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         (BlobContainerClient container, string path) = await GetPartsAsync(fullPath, true).ConfigureAwait(false);

         if(container == null)
            return false;

         BlobBaseClient client = container.GetBlobBaseClient(path);

         try
         {
            await client.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
         }
         catch(RequestFailedException ex) when(ex.ErrorCode == "BlobNotFound")
         {
            return false;
         }

         return true;
      }

      private async Task<(BlobContainerClient, string)> GetPartsAsync(string fullPath, bool createContainer = true)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         fullPath = StoragePath.Normalize(fullPath);
         if(fullPath == null)
            throw new ArgumentNullException(nameof(fullPath));

         string containerName, relativePath;

         if(_containerName == null)
         {
            int idx = fullPath.IndexOf(StoragePath.PathSeparator);
            if(idx == -1)
            {
               containerName = fullPath;
               relativePath = string.Empty;
            }
            else
            {
               containerName = fullPath.Substring(0, idx);
               relativePath = fullPath.Substring(idx + 1);
            }
         }
         else
         {
            containerName = _containerName;
            relativePath = fullPath;
         }

         if(!_containerNameToContainerClient.TryGetValue(containerName, out BlobContainerClient container))
         {
            container = _client.GetBlobContainerClient(containerName);
            if(_containerName == null)
            {
               try
               {
                  //check if container exists
                  await container.GetPropertiesAsync().ConfigureAwait(false);

               }
               catch(RequestFailedException ex) when (ex.ErrorCode == "ContainerNotFound")
               {
                  if(createContainer)
                  {
                     await container.CreateIfNotExistsAsync().ConfigureAwait(false);
                  }
                  else
                  {
                     return (null, null);
                  }
               }
            }

            _containerNameToContainerClient[containerName] = container;
         }

         return (container, relativePath);
      }

   }
}
