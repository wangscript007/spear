﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Spear.Core.Message;
using Spear.Core.Session;
using Spear.ProxyGenerator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web;

namespace Spear.Core.Micro.Implementation
{
    /// <inheritdoc />
    /// <summary> 默认服务执行者 </summary>
    public class MicroExecutor : IMicroExecutor
    {
        private readonly ILogger<MicroExecutor> _logger;
        private readonly IMicroEntryFactory _entryFactory;
        private readonly IServiceProvider _provider;
        public MicroExecutor(ILogger<MicroExecutor> logger, IServiceProvider provider, IMicroEntryFactory entryFactory)
        {
            _entryFactory = entryFactory;
            _logger = logger;
            _provider = provider;
        }

        private async Task LocalExecute(InvokeMessage invokeMessage, ResultMessage result)
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(JsonConvert.SerializeObject(invokeMessage));
                var serviceMethod = _entryFactory.Find(invokeMessage.ServiceId);
                var args = new List<object>();
                var parameters = invokeMessage.Parameters ?? new Dictionary<string, object>();
                foreach (var parameter in serviceMethod.GetParameters())
                {
                    if (parameters.ContainsKey(parameter.Name))
                    {
                        var parameterType = parameter.ParameterType;
                        var arg = parameters[parameter.Name].CastTo(parameterType);
                        args.Add(arg);
                    }
                    else if (parameter.HasDefaultValue)
                    {
                        args.Add(parameter.DefaultValue);
                    }
                }

                var instance = _provider.GetService(serviceMethod.DeclaringType);

                var fastInvoke = FastInvoke.GetMethodInvoker(serviceMethod);
                if (serviceMethod.ReturnType == typeof(void) || serviceMethod.ReturnType == typeof(Task))
                {
                    fastInvoke(instance, args.ToArray());
                }
                else
                {
                    var data = fastInvoke(instance, args.ToArray());
                    if (!(data is Task task))
                    {
                        result.Data = data;
                    }
                    else
                    {
                        await task;
                        var taskType = task.GetType().GetTypeInfo();
                        if (taskType.IsGenericType)
                        {
                            var prop = taskType.GetProperty("Result");
                            if (prop != null)
                                result.Data = prop.GetValue(task);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行本地逻辑时候发生了错误。");
                result.Message = ex.Message;
                result.Code = 500;
            }
        }

        private async Task SendResult(IMessageSender sender, string messageId, ResultMessage result)
        {
            try
            {
                await sender.Send(result.Create(messageId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送响应消息异常");
            }
        }

        public async Task Execute(IMessageSender sender, MicroMessage message)
        {
            if (!message.IsInvoke)
                return;
            var invokeMessage = message.GetContent<InvokeMessage>();
            var accessor = _provider.GetService<IPrincipalAccessor>();
            if (accessor != null && invokeMessage.Headers != null && invokeMessage.Headers.Any())
            {
                var session = new MicroSessionDto();
                //解析Claims
                if (invokeMessage.Headers.TryGetValue(MicroClaimTypes.HeaderUserId, out var userId))
                    session.UserId = userId;
                if (invokeMessage.Headers.TryGetValue(MicroClaimTypes.HeaderTenantId, out var tenantId))
                    session.TenantId = tenantId;
                //username
                if (invokeMessage.Headers.TryGetValue(MicroClaimTypes.HeaderUserName, out var userName))
                    session.UserName = HttpUtility.UrlDecode(userName);
                //role
                if (invokeMessage.Headers.TryGetValue(MicroClaimTypes.HeaderRole, out var role))
                    session.Role = HttpUtility.UrlDecode(role);
                accessor.SetSession(session);
            }
            var result = new ResultMessage();
            if (invokeMessage.IsNotice)
            {
                //向客户端发送结果
                await SendResult(sender, message.Id, result);

                //确保新起一个线程执行，不堵塞当前线程
                await Task.Factory.StartNew(async () =>
                {
                    //执行本地代码
                    await LocalExecute(invokeMessage, result);
                }, TaskCreationOptions.LongRunning);
                return;
            }

            //执行本地代码
            await LocalExecute(invokeMessage, result);
            //向客户端发送结果
            await SendResult(sender, message.Id, result);
        }
    }
}
