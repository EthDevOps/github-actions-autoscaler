﻿// <auto-generated />
using System;
using GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    [DbContext(typeof(ActionsRunnerContext))]
    [Migration("20240725151801_InitialCreate")]
    partial class InitialCreate
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.7")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.Property<int>("RunnerId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RunnerId"));

                    b.Property<string>("Cloud")
                        .HasColumnType("text");

                    b.Property<string>("Hostname")
                        .HasColumnType("text");

                    b.Property<string>("Profile")
                        .HasColumnType("text");

                    b.Property<string>("Size")
                        .HasColumnType("text");

                    b.Property<int?>("WorkflowId")
                        .HasColumnType("integer");

                    b.HasKey("RunnerId");

                    b.HasIndex("WorkflowId");

                    b.ToTable("Runners");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.RunnerLifecycle", b =>
                {
                    b.Property<int>("RunnerLifecycleId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RunnerLifecycleId"));

                    b.Property<string>("Event")
                        .HasColumnType("text");

                    b.Property<DateTime>("EventTimeUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("RunnerId")
                        .HasColumnType("integer");

                    b.Property<int>("Status")
                        .HasColumnType("integer");

                    b.HasKey("RunnerLifecycleId");

                    b.HasIndex("RunnerId");

                    b.ToTable("RunnerLifecycles");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Workflow", b =>
                {
                    b.Property<int>("WorkflowId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("WorkflowId"));

                    b.Property<long>("GithubWorkflowId")
                        .HasColumnType("bigint");

                    b.Property<string>("Owner")
                        .HasColumnType("text");

                    b.Property<string>("Repository")
                        .HasColumnType("text");

                    b.HasKey("WorkflowId");

                    b.ToTable("Workflows");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.HasOne("GithubActionsOrchestrator.Database.Workflow", "Workflow")
                        .WithMany()
                        .HasForeignKey("WorkflowId");

                    b.Navigation("Workflow");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.RunnerLifecycle", b =>
                {
                    b.HasOne("GithubActionsOrchestrator.Database.Runner", null)
                        .WithMany("Lifecycle")
                        .HasForeignKey("RunnerId");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.Navigation("Lifecycle");
                });
#pragma warning restore 612, 618
        }
    }
}
