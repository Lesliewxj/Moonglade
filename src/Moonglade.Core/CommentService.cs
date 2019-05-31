﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using System.Web;
using Edi.Practice.RequestResponseModel;
using Edi.WordFilter;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moonglade.Configuration.Abstraction;
using Moonglade.Data.Entities;
using Moonglade.Data.Infrastructure;
using Moonglade.Data.Spec;
using Moonglade.Model;
using Moonglade.Model.Settings;

namespace Moonglade.Core
{
    public class CommentService : MoongladeService
    {
        private readonly IBlogConfig _blogConfig;

        private readonly IRepository<CommentEntity> _commentRepository;

        private readonly IRepository<CommentReplyEntity> _commentReplyRepository;

        public CommentService(
            ILogger<CommentService> logger,
            IOptions<AppSettings> settings,
            IBlogConfig blogConfig,
            IRepository<CommentEntity> commentRepository,
            IRepository<CommentReplyEntity> commentReplyRepository) : base(logger, settings)
        {
            _blogConfig = blogConfig;

            _commentRepository = commentRepository;
            _commentReplyRepository = commentReplyRepository;
        }

        public int CountComments()
        {
            return _commentRepository.Count(c => true);
        }

        public Task<IReadOnlyList<PostCommentListItem>> GetSelectedCommentsOfPostAsync(Guid postId)
        {
            return _commentRepository.SelectAsync(new CommentSepc(postId), c => new PostCommentListItem
            {
                CommentContent = c.CommentContent,
                CreateOnUtc = c.CreateOnUtc,
                Username = c.Username,
                CommentReplies = c.CommentReply.Select(cr => new CommentReplyItem
                {
                    ReplyContent = cr.ReplyContent,
                    ReplyTimeUtc = cr.ReplyTimeUtc.GetValueOrDefault()
                }).ToList()
            });
        }

        public Task<Response<IReadOnlyList<CommentListItem>>> GetPagedCommentAsync(int pageSize, int pageIndex)
        {
            return TryExecuteAsync<IReadOnlyList<CommentListItem>>(async () =>
            {
                if (pageSize < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageSize), $"{nameof(pageSize)} can not be less than 1.");
                }

                var spec = new CommentSepc(pageSize, pageIndex);
                var comments = await _commentRepository.SelectAsync(spec, p => new CommentListItem
                {
                    Id = p.Id,
                    CommentContent = p.CommentContent,
                    CreateOnUtc = p.CreateOnUtc,
                    Email = p.Email,
                    IpAddress = p.IPAddress,
                    Username = p.Username,
                    IsApproved = p.IsApproved,
                    PostTitle = p.Post.Title,
                    CommentReplies = p.CommentReply.Select(cr => new CommentReplyItem
                    {
                        ReplyContent = cr.ReplyContent,
                        ReplyTimeUtc = cr.ReplyTimeUtc.GetValueOrDefault()
                    }).ToList()
                });

                return new SuccessResponse<IReadOnlyList<CommentListItem>>(comments);
            });
        }

        public Task<Response> ToggleCommentApprovalStatus(Guid[] commentIds)
        {
            return TryExecuteAsync(async () =>
            {
                if (null == commentIds || !commentIds.Any())
                {
                    throw new ArgumentNullException(nameof(commentIds));
                }

                var spec = new CommentSepc(commentIds);
                var comments = await _commentRepository.GetAsync(spec);
                foreach (var cmt in comments)
                {
                    cmt.IsApproved = !cmt.IsApproved;
                    await _commentRepository.UpdateAsync(cmt);
                }

                return new SuccessResponse();
            });
        }

        public Task<Response> DeleteComments(Guid[] commentIds)
        {
            return TryExecuteAsync(async () =>
            {
                var spec = new CommentSepc(commentIds);
                var comments = await _commentRepository.GetAsync(spec);
                foreach (var cmt in comments)
                {
                    // 1. Delete all replies
                    var cReplies = await _commentReplyRepository.GetAsync(new CommentReplySpec(cmt.Id));
                    if (cReplies.Any())
                    {
                        _commentReplyRepository.Delete(cReplies);
                    }

                    // 2. Delete comment itself
                    _commentRepository.Delete(cmt);
                }

                return new SuccessResponse();
            });
        }

        public Task<Response<CommentEntity>> AddCommentAsync(NewCommentRequest request)
        {
            return TryExecuteAsync<CommentEntity>(async () =>
            {
                // 1. Check comment enabled or not
                if (!_blogConfig.ContentSettings.EnableComments)
                {
                    return new FailedResponse<CommentEntity>((int)ResponseFailureCode.CommentDisabled);
                }

                // 2. Check user email domain
                var bannedDomains = _blogConfig.EmailSettings.BannedMailDomain;
                if (bannedDomains.Any())
                {
                    var address = new MailAddress(request.Email);
                    if (bannedDomains.Contains(address.Host))
                    {
                        return new FailedResponse<CommentEntity>((int)ResponseFailureCode.EmailDomainBlocked);
                    }
                }

                // 3. Encode HTML
                request.Username = HttpUtility.HtmlEncode(request.Username);

                // 4. Harmonize banned keywords
                if (_blogConfig.ContentSettings.EnableWordFilter)
                {
                    var dw = _blogConfig.ContentSettings.DisharmonyWords;
                    var maskWordFilter = new MaskWordFilter(new StringWordSource(dw));
                    request.Username = maskWordFilter.FilterContent(request.Username);
                    request.Content = maskWordFilter.FilterContent(request.Content);
                }

                var model = new CommentEntity
                {
                    Id = Guid.NewGuid(),
                    Username = request.Username,
                    CommentContent = request.Content,
                    PostId = request.PostId,
                    CreateOnUtc = DateTime.UtcNow,
                    Email = request.Email,
                    IPAddress = request.IpAddress,
                    IsApproved = false,
                    UserAgent = request.UserAgent
                };

                await _commentRepository.AddAsync(model);
                return new SuccessResponse<CommentEntity>(model);
            });
        }

        public Response<CommentReplySummary> AddReply(Guid commentId, string replyContent, string ipAddress, string userAgent)
        {
            return TryExecute<CommentReplySummary>(() =>
            {
                if (!_blogConfig.ContentSettings.EnableComments)
                {
                    return new FailedResponse<CommentReplySummary>((int)ResponseFailureCode.CommentDisabled);
                }

                var cmt = _commentRepository.Get(commentId);

                if (null == cmt)
                {
                    return new FailedResponse<CommentReplySummary>((int)ResponseFailureCode.CommentNotFound);
                }

                var id = Guid.NewGuid();
                var model = new CommentReplyEntity
                {
                    Id = id,
                    ReplyContent = replyContent,
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    ReplyTimeUtc = DateTime.UtcNow,
                    CommentId = commentId
                };

                _commentReplyRepository.Add(model);

                var summary = new CommentReplySummary
                {
                    CommentContent = cmt.CommentContent,
                    CommentId = commentId,
                    Email = cmt.Email,
                    Id = model.Id,
                    IpAddress = model.IpAddress,
                    PostId = cmt.PostId,
                    PubDateUtc = cmt.Post.PostPublish.PubDateUtc,
                    ReplyContent = model.ReplyContent,
                    ReplyTimeUtc = model.ReplyTimeUtc,
                    Slug = cmt.Post.Slug,
                    Title = cmt.Post.Title,
                    UserAgent = model.UserAgent
                };

                return new SuccessResponse<CommentReplySummary>(summary);
            });
        }
    }
}
