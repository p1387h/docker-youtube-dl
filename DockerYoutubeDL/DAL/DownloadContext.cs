using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadContext : DbContext
    {
        public DbSet<DownloadTask> DownloadTask { get; set; }

        public DownloadContext()
        {
        }

        public DownloadContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configuratio of the DownloadTask table.
            modelBuilder.Entity<DownloadTask>()
                .HasKey(x => x.Id);
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Id)
                .IsRequired();
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Url)
                .IsRequired();
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Status)
                .HasDefaultValue(DownloadTaskStatus.Waiting);
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Downloader)
                .IsRequired();
        }
    }
}
