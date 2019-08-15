﻿using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Tapeti.Config;

namespace Tapeti.Flow.Default
{
    /// <inheritdoc />
    /// <summary>
    /// Default implementation for IFlowStarter.
    /// </summary>
    internal class FlowStarter : IFlowStarter
    {
        private readonly ITapetiConfig config;
        private readonly ILogger logger;


        /// <inheritdoc />
        public FlowStarter(ITapetiConfig config, ILogger logger)
        {
            this.config = config;
            this.logger = logger;
        }


        /// <inheritdoc />
        public Task Start<TController>(Expression<Func<TController, Func<IYieldPoint>>> methodSelector) where TController : class
        {
            return CallControllerMethod<TController>(GetExpressionMethod(methodSelector), value => Task.FromResult((IYieldPoint)value), new object[] { });
        }

        /// <inheritdoc />
        public Task Start<TController>(Expression<Func<TController, Func<Task<IYieldPoint>>>> methodSelector) where TController : class
        {
            return CallControllerMethod<TController>(GetExpressionMethod(methodSelector), value => (Task<IYieldPoint>)value, new object[] {});
        }

        /// <inheritdoc />
        public Task Start<TController, TParameter>(Expression<Func<TController, Func<TParameter, IYieldPoint>>> methodSelector, TParameter parameter) where TController : class
        {
            return CallControllerMethod<TController>(GetExpressionMethod(methodSelector), value => Task.FromResult((IYieldPoint)value), new object[] {parameter});
        }

        /// <inheritdoc />
        public Task Start<TController, TParameter>(Expression<Func<TController, Func<TParameter, Task<IYieldPoint>>>> methodSelector, TParameter parameter) where TController : class
        {
            return CallControllerMethod<TController>(GetExpressionMethod(methodSelector), value => (Task<IYieldPoint>)value, new object[] {parameter});
        }


        private async Task CallControllerMethod<TController>(MethodBase method, Func<object, Task<IYieldPoint>> getYieldPointResult, object[] parameters) where TController : class
        {
            var controller = config.DependencyResolver.Resolve<TController>();
            var yieldPoint = await getYieldPointResult(method.Invoke(controller, parameters));

            /*
            var context = new ControllerMessageContext()
            {
                Config = config,
                Controller = controller
            };
            */

            var flowHandler = config.DependencyResolver.Resolve<IFlowHandler>();

            try
            {
                //await flowHandler.Execute(context, yieldPoint);
                //handlingResult.ConsumeResponse = ConsumeResponse.Ack;
            }
            finally
            {
                //await RunCleanup(context, handlingResult.ToHandlingResult());
            }
        }

        /*
        private async Task RunCleanup(MessageContext context, HandlingResult handlingResult)
        {
            foreach (var handler in config.CleanupMiddleware)
            {
                try
                {
                    await handler.Handle(context, handlingResult);
                }
                catch (Exception eCleanup)
                {
                    logger.HandlerException(eCleanup);
                }
            }
        }
        */


        private static MethodInfo GetExpressionMethod<TController, TResult>(Expression<Func<TController, Func<TResult>>> methodSelector)
        {
            var callExpression = (methodSelector.Body as UnaryExpression)?.Operand as MethodCallExpression;
            var targetMethodExpression = callExpression?.Object as ConstantExpression;

            var method = targetMethodExpression?.Value as MethodInfo;
            if (method == null)
                throw new ArgumentException("Unable to determine the starting method", nameof(methodSelector));

            return method;
        }

        private static MethodInfo GetExpressionMethod<TController, TResult, TParameter>(Expression<Func<TController, Func<TParameter, TResult>>> methodSelector)
        {
            var callExpression = (methodSelector.Body as UnaryExpression)?.Operand as MethodCallExpression;
            var targetMethodExpression = callExpression?.Object as ConstantExpression;

            var method = targetMethodExpression?.Value as MethodInfo;
            if (method == null)
                throw new ArgumentException("Unable to determine the starting method", nameof(methodSelector));

            return method;
        }
    }
}
