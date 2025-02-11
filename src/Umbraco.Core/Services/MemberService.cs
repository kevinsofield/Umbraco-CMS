﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Web.Security;
using System.Xml.Linq;
using Umbraco.Core.Auditing;
using Umbraco.Core.Configuration;
using Umbraco.Core.Events;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;
using System.Linq;
using Umbraco.Core.Security;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the MemberService.
    /// </summary>
    public class MemberService : RepositoryService, IMemberService
    {
        private readonly IMemberGroupService _memberGroupService;
        private readonly EntityXmlSerializer _entitySerializer = new EntityXmlSerializer();
        private readonly IDataTypeService _dataTypeService;
        private static readonly ReaderWriterLockSlim Locker = new ReaderWriterLockSlim();

        [Obsolete("Use the constructors that specify all dependencies instead")]
        public MemberService(RepositoryFactory repositoryFactory, IMemberGroupService memberGroupService)
            : this(new PetaPocoUnitOfWorkProvider(), repositoryFactory, memberGroupService)
        {
        }

        [Obsolete("Use the constructors that specify all dependencies instead")]
        public MemberService(IDatabaseUnitOfWorkProvider provider, IMemberGroupService memberGroupService)
            : this(provider, new RepositoryFactory(), memberGroupService)
        {
        }

        [Obsolete("Use the constructors that specify all dependencies instead")]
        public MemberService(IDatabaseUnitOfWorkProvider provider, RepositoryFactory repositoryFactory, IMemberGroupService memberGroupService)
            : base(provider, repositoryFactory, LoggerResolver.Current.Logger)
        {
            if (memberGroupService == null) throw new ArgumentNullException("memberGroupService");
            _memberGroupService = memberGroupService;
            _dataTypeService = new DataTypeService(provider, repositoryFactory);
        }

        [Obsolete("Use the constructors that specify all dependencies instead")]
        public MemberService(IDatabaseUnitOfWorkProvider provider, IMemberGroupService memberGroupService, IDataTypeService dataTypeService)
            : this(provider, new RepositoryFactory(), LoggerResolver.Current.Logger, memberGroupService, dataTypeService)
        {

        }

        public MemberService(IDatabaseUnitOfWorkProvider provider, RepositoryFactory repositoryFactory, ILogger logger, IMemberGroupService memberGroupService, IDataTypeService dataTypeService)
            : base(provider, repositoryFactory, logger)
        {
            if (memberGroupService == null) throw new ArgumentNullException("memberGroupService");
            if (dataTypeService == null) throw new ArgumentNullException("dataTypeService");
            _memberGroupService = memberGroupService;
            _dataTypeService = dataTypeService;
        }

        #region IMemberService Implementation

        /// <summary>
        /// Gets the default MemberType alias
        /// </summary>
        /// <remarks>By default we'll return the 'writer', but we need to check it exists. If it doesn't we'll 
        /// return the first type that is not an admin, otherwise if there's only one we will return that one.</remarks>
        /// <returns>Alias of the default MemberType</returns>
        public string GetDefaultMemberType()
        {
            using (var repository = RepositoryFactory.CreateMemberTypeRepository(UowProvider.GetUnitOfWork()))
            {
                var types = repository.GetAll(new int[]{}).Select(x => x.Alias).ToArray();

                if (types.Any() == false)
                {
                    throw new InvalidOperationException("No member types could be resolved");
                }

                if (types.InvariantContains("Member"))
                {
                    return types.First(x => x.InvariantEquals("Member"));
                }

                return types.First();
            }
        }

        /// <summary>
        /// Checks if a Member with the username exists
        /// </summary>
        /// <param name="username">Username to check</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(string username)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                return repository.Exists(username);
            }
        }

        /// <summary>
        /// This is simply a helper method which essentially just wraps the MembershipProvider's ChangePassword method
        /// </summary>
        /// <remarks>This method exists so that Umbraco developers can use one entry point to create/update 
        /// Members if they choose to. </remarks>
        /// <param name="member">The Member to save the password for</param>
        /// <param name="password">The password to encrypt and save</param>
        public void SavePassword(IMember member, string password)
        {
            if (member == null) throw new ArgumentNullException("member");

            var provider = MembershipProviderExtensions.GetMembersMembershipProvider();
            if (provider.IsUmbracoMembershipProvider())
            {
                provider.ChangePassword(member.Username, "", password);
            }
            else
            {
                throw new NotSupportedException("When using a non-Umbraco membership provider you must change the member password by using the MembershipProvider.ChangePassword method");
            }

            //go re-fetch the member and update the properties that may have changed
            var result = GetByUsername(member.Username);

            //should never be null but it could have been deleted by another thread.
            if (result == null)
                return;

            member.RawPasswordValue = result.RawPasswordValue;
            member.LastPasswordChangeDate = result.LastPasswordChangeDate;
            member.UpdateDate = member.UpdateDate;
        }

        /// <summary>
        /// Checks if a Member with the id exists
        /// </summary>
        /// <param name="id">Id of the Member</param>
        /// <returns><c>True</c> if the Member exists otherwise <c>False</c></returns>
        public bool Exists(int id)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                return repository.Exists(id);
            }
        }

        /// <summary>
        /// Gets a Member by its integer id
        /// </summary>
        /// <param name="id"><see cref="System.int"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetById(int id)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                return repository.Get(id);
            }
        }

        /// <summary>
        /// Gets a Member by the unique key
        /// </summary>
        /// <remarks>The guid key corresponds to the unique id in the database
        /// and the user id in the membership provider.</remarks>
        /// <param name="id"><see cref="Guid"/> Id</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByKey(Guid id)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                var query = Query<IMember>.Builder.Where(x => x.Key == id);
                var member = repository.GetByQuery(query).FirstOrDefault();
                return member;
            }
        }

        /// <summary>
        /// Gets all Members for the specified MemberType alias
        /// </summary>
        /// <param name="memberTypeAlias">Alias of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(string memberTypeAlias)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                var query = Query<IMember>.Builder.Where(x => x.ContentTypeAlias == memberTypeAlias);
                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Gets all Members for the MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByMemberType(int memberTypeId)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                repository.Get(memberTypeId);
                var query = Query<IMember>.Builder.Where(x => x.ContentTypeId == memberTypeId);
                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Gets all Members within the specified MemberGroup name
        /// </summary>
        /// <param name="memberGroupName">Name of the MemberGroup</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByGroup(string memberGroupName)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                return repository.GetByMemberGroup(memberGroupName);
            }
        }

        /// <summary>
        /// Gets all Members with the ids specified
        /// </summary>
        /// <remarks>If no Ids are specified all Members will be retrieved</remarks>
        /// <param name="ids">Optional list of Member Ids</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAllMembers(params int[] ids)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                return repository.GetAll(ids);
            }
        }

        /// <summary>
        /// Delete Members of the specified MemberType id
        /// </summary>
        /// <param name="memberTypeId">Id of the MemberType</param>
        public void DeleteMembersOfType(int memberTypeId)
        {
            using (new WriteLock(Locker))
            {
                using (var uow = UowProvider.GetUnitOfWork())
                {
                    var repository = RepositoryFactory.CreateMemberRepository(uow);
                    //TODO: What about content that has the contenttype as part of its composition?
                    var query = Query<IMember>.Builder.Where(x => x.ContentTypeId == memberTypeId);
                    var members = repository.GetByQuery(query).ToArray();

                    if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMember>(members), this))
                        return;

                    foreach (var member in members)
                    {
                        //Permantly delete the member
                        Delete(member);
                    }
                }
            }
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindMembersByDisplayName(string displayNameToMatch, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindMembersByDisplayName(displayNameToMatch, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds Members based on their display name
        /// </summary>
        /// <param name="displayNameToMatch">Display name to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindMembersByDisplayName(string displayNameToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                var query = new Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Name.Equals(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Name.Contains(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Name.StartsWith(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Name.EndsWith(displayNameToMatch));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Name.SqlWildcard(displayNameToMatch, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "Name", Direction.Ascending);
            }
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindByEmail(string emailStringToMatch, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindByEmail(emailStringToMatch, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial email string
        /// </summary>
        /// <param name="emailStringToMatch">Partial email string to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByEmail(string emailStringToMatch, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                var query = new Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Email.Equals(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Email.Contains(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Email.StartsWith(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Email.EndsWith(emailStringToMatch));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Email.SqlWildcard(emailStringToMatch, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "Email", Direction.Ascending);
            }
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> FindByUsername(string login, int pageIndex, int pageSize, out int totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            long total;
            var result = FindByUsername(login, Convert.ToInt64(pageIndex), pageSize, out total, matchType);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Finds a list of <see cref="IMember"/> objects by a partial username
        /// </summary>
        /// <param name="login">Partial username to match</param>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.StartsWith"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> FindByUsername(string login, long pageIndex, int pageSize, out long totalRecords, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                var query = new Query<IMember>();

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query.Where(member => member.Username.Equals(login));
                        break;
                    case StringPropertyMatchType.Contains:
                        query.Where(member => member.Username.Contains(login));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query.Where(member => member.Username.StartsWith(login));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query.Where(member => member.Username.EndsWith(login));
                        break;
                    case StringPropertyMatchType.Wildcard:
                        query.Where(member => member.Email.SqlWildcard(login, TextColumnType.NVarchar));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, "LoginName", Direction.Ascending);
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.string"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, string value, StringPropertyMatchType matchType = StringPropertyMatchType.Exact)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                IQuery<IMember> query;

                switch (matchType)
                {
                    case StringPropertyMatchType.Exact:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                (((Member)x).LongStringPropertyValue.SqlEquals(value, TextColumnType.NText) ||
                                 ((Member)x).ShortStringPropertyValue.SqlEquals(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.Contains:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                (((Member)x).LongStringPropertyValue.SqlContains(value, TextColumnType.NText) ||
                                 ((Member)x).ShortStringPropertyValue.SqlContains(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.StartsWith:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                (((Member)x).LongStringPropertyValue.SqlStartsWith(value, TextColumnType.NText) ||
                                 ((Member)x).ShortStringPropertyValue.SqlStartsWith(value, TextColumnType.NVarchar)));
                        break;
                    case StringPropertyMatchType.EndsWith:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                (((Member)x).LongStringPropertyValue.SqlEndsWith(value, TextColumnType.NText) ||
                                 ((Member)x).ShortStringPropertyValue.SqlEndsWith(value, TextColumnType.NVarchar)));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.int"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, int value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                IQuery<IMember> query;

                switch (matchType)
                {
                    case ValuePropertyMatchType.Exact:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).IntegerPropertyValue == value);
                        break;
                    case ValuePropertyMatchType.GreaterThan:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).IntegerPropertyValue > value);
                        break;
                    case ValuePropertyMatchType.LessThan:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).IntegerPropertyValue < value);
                        break;
                    case ValuePropertyMatchType.GreaterThanOrEqualTo:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).IntegerPropertyValue >= value);
                        break;
                    case ValuePropertyMatchType.LessThanOrEqualTo:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).IntegerPropertyValue <= value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.bool"/> Value to match</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, bool value)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                var query =
                    Query<IMember>.Builder.Where(
                        x =>
                            ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                            ((Member)x).BoolPropertyValue == value);

                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Gets a list of Members based on a property search
        /// </summary>
        /// <param name="propertyTypeAlias">Alias of the PropertyType to search for</param>
        /// <param name="value"><see cref="System.DateTime"/> Value to match</param>
        /// <param name="matchType">The type of match to make as <see cref="StringPropertyMatchType"/>. Default is <see cref="StringPropertyMatchType.Exact"/></param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetMembersByPropertyValue(string propertyTypeAlias, DateTime value, ValuePropertyMatchType matchType = ValuePropertyMatchType.Exact)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                IQuery<IMember> query;

                switch (matchType)
                {
                    case ValuePropertyMatchType.Exact:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).DateTimePropertyValue == value);
                        break;
                    case ValuePropertyMatchType.GreaterThan:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).DateTimePropertyValue > value);
                        break;
                    case ValuePropertyMatchType.LessThan:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).DateTimePropertyValue < value);
                        break;
                    case ValuePropertyMatchType.GreaterThanOrEqualTo:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).DateTimePropertyValue >= value);
                        break;
                    case ValuePropertyMatchType.LessThanOrEqualTo:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == propertyTypeAlias &&
                                ((Member)x).DateTimePropertyValue <= value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("matchType");
                }

                //TODO: Since this is by property value, we need a GetByPropertyQuery on the repo!
                var members = repository.GetByQuery(query);
                return members;
            }
        }

        /// <summary>
        /// Rebuilds all xml content in the cmsContentXml table for all members
        /// </summary>
        /// <param name="memberTypeIds">
        /// Only rebuild the xml structures for the content type ids passed in, if none then rebuilds the structures
        /// for all members = USE WITH CARE!
        /// </param>
        /// <returns>True if publishing succeeded, otherwise False</returns>
        public void RebuildXmlStructures(params int[] memberTypeIds)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                repository.RebuildXmlStructures(
                    member => _entitySerializer.Serialize(_dataTypeService, member),
                    contentTypeIds: memberTypeIds.Length == 0 ? null : memberTypeIds);
            }

            Audit(AuditType.Publish, "MemberService.RebuildXmlStructures completed, the xml has been regenerated in the database", 0, -1);
        }

        #endregion

        #region IMembershipMemberService Implementation

        /// <summary>
        /// Gets the total number of Members based on the count type
        /// </summary>
        /// <remarks>
        /// The way the Online count is done is the same way that it is done in the MS SqlMembershipProvider - We query for any members
        /// that have their last active date within the Membership.UserIsOnlineTimeWindow (which is in minutes). It isn't exact science
        /// but that is how MS have made theirs so we'll follow that principal.
        /// </remarks>
        /// <param name="countType"><see cref="MemberCountType"/> to count by</param>
        /// <returns><see cref="System.int"/> with number of Members for passed in type</returns>
        public int GetCount(MemberCountType countType)
        {
            using (var repository = RepositoryFactory.CreateMemberRepository(UowProvider.GetUnitOfWork()))
            {
                IQuery<IMember> query;

                switch (countType)
                {
                    case MemberCountType.All:
                        query = new Query<IMember>();
                        return repository.Count(query);
                    case MemberCountType.Online:
                        var fromDate = DateTime.Now.AddMinutes(-Membership.UserIsOnlineTimeWindow);
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.LastLoginDate &&
                                ((Member)x).DateTimePropertyValue > fromDate);
                        return repository.GetCountByQuery(query);
                    case MemberCountType.LockedOut:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsLockedOut &&
                                ((Member)x).BoolPropertyValue == true);
                        return repository.GetCountByQuery(query);
                    case MemberCountType.Approved:
                        query =
                            Query<IMember>.Builder.Where(
                                x =>
                                ((Member)x).PropertyTypeAlias == Constants.Conventions.Member.IsApproved &&
                                ((Member)x).BoolPropertyValue == true);
                        return repository.GetCountByQuery(query);
                    default:
                        throw new ArgumentOutOfRangeException("countType");
                }
            }

        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> GetAll(int pageIndex, int pageSize, out int totalRecords)
        {
            long total;
            var result = GetAll(Convert.ToInt64(pageIndex), pageSize, out total);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        /// <summary>
        /// Gets a list of paged <see cref="IMember"/> objects
        /// </summary>
        /// <param name="pageIndex">Current page index</param>
        /// <param name="pageSize">Size of the page</param>
        /// <param name="totalRecords">Total number of records found (out)</param>
        /// <returns><see cref="IEnumerable{IMember}"/></returns>
        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                return repository.GetPagedResultsByQuery(null, pageIndex, pageSize, out totalRecords, "LoginName", Direction.Ascending);
            }
        }

        [Obsolete("Use the overload with 'long' parameter types instead")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<IMember> GetAll(int pageIndex, int pageSize, out int totalRecords,
            string orderBy, Direction orderDirection, string memberTypeAlias = null, string filter = "")
        {
            long total;
            var result = GetAll(Convert.ToInt64(pageIndex), pageSize, out total, orderBy, orderDirection, memberTypeAlias, filter);
            totalRecords = Convert.ToInt32(total);
            return result;
        }

        public IEnumerable<IMember> GetAll(long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, string memberTypeAlias = null, string filter = "")
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                if (memberTypeAlias == null)
                {
                    return repository.GetPagedResultsByQuery(null, pageIndex, pageSize, out totalRecords, orderBy, orderDirection, filter);
                }
                var query = new Query<IMember>().Where(x => x.ContentTypeAlias == memberTypeAlias);
                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalRecords, orderBy, orderDirection, filter);
            }
        }

        /// <summary>
        /// Gets the count of Members by an optional MemberType alias
        /// </summary>
        /// <remarks>If no alias is supplied then the count for all Member will be returned</remarks>
        /// <param name="memberTypeAlias">Optional alias for the MemberType when counting number of Members</param>
        /// <returns><see cref="System.int"/> with number of Members</returns>
        public int Count(string memberTypeAlias = null)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                return repository.Count(memberTypeAlias);
            }
        }

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, string memberTypeAlias)
        {
            var memberType = FindMemberTypeByAlias(memberTypeAlias);
            return CreateMember(username, email, name, memberType);
        }

        /// <summary>
        /// Creates an <see cref="IMember"/> object without persisting it
        /// </summary>
        /// <remarks>This method is convenient for when you need to add properties to a new Member
        /// before persisting it in order to limit the amount of times its saved.
        /// Also note that the returned <see cref="IMember"/> will not have an Id until its saved.</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMember(string username, string email, string name, IMemberType memberType)
        {
            var member = new Member(name, email.ToLower().Trim(), username, memberType);

            Created.RaiseEvent(new NewEventArgs<IMember>(member, false, memberType.Alias, -1), this);

            return member;
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberTypeAlias">Alias of the MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, string memberTypeAlias)
        {
            var memberType = FindMemberTypeByAlias(memberTypeAlias);
            return CreateMemberWithIdentity(username, email, name, memberType);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, IMemberType memberType)
        {
            return CreateMemberWithIdentity(username, email, username, memberType);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember CreateMemberWithIdentity(string username, string email, string name, IMemberType memberType)
        {
            return CreateMemberWithIdentity(username, email, name, "", memberType);
        }

        /// <summary>
        /// Creates and persists a new <see cref="IMember"/>
        /// </summary>
        /// <remarks>An <see cref="IMembershipUser"/> can be of type <see cref="IMember"/> or <see cref="IUser"/></remarks>
        /// <param name="username">Username of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="email">Email of the <see cref="IMembershipUser"/> to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberTypeAlias">Alias of the Type</param>
        /// <returns><see cref="IMember"/></returns>
        IMember IMembershipMemberService<IMember>.CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias)
        {
            var memberType = FindMemberTypeByAlias(memberTypeAlias);
            return CreateMemberWithIdentity(username, email, username, passwordValue, memberType);
        }

        /// <summary>
        /// Creates and persists a Member
        /// </summary>
        /// <remarks>Using this method will persist the Member object before its returned 
        /// meaning that it will have an Id available (unlike the CreateMember method)</remarks>
        /// <param name="username">Username of the Member to create</param>
        /// <param name="email">Email of the Member to create</param>
        /// <param name="name">Name of the Member to create</param>
        /// <param name="passwordValue">This value should be the encoded/encrypted/hashed value for the password that will be stored in the database</param>
        /// <param name="memberType">MemberType the Member should be based on</param>
        /// <returns><see cref="IMember"/></returns>
        private IMember CreateMemberWithIdentity(string username, string email, string name, string passwordValue, IMemberType memberType)
        {
            if (memberType == null) throw new ArgumentNullException("memberType");

            var member = new Member(name, email.ToLower().Trim(), username, passwordValue, memberType);

            if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(member), this))
            {
                member.WasCancelled = true;
                return member;
            }

            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                repository.AddOrUpdate(member);
                //insert the xml
                repository.AddOrUpdateContentXml(member, m => _entitySerializer.Serialize(_dataTypeService, m));
                // generate preview for blame history?
                if (UmbracoConfig.For.UmbracoSettings().Content.GlobalPreviewStorageEnabled)
                {
                    repository.AddOrUpdatePreviewXml(member, m => _entitySerializer.Serialize(_dataTypeService, m));
                }

                uow.Commit();
            }

            Saved.RaiseEvent(new SaveEventArgs<IMember>(member, false), this);
            Created.RaiseEvent(new NewEventArgs<IMember>(member, false, memberType.Alias, -1), this);

            return member;
        }

        /// <summary>
        /// Gets an <see cref="IMember"/> by its provider key
        /// </summary>
        /// <param name="id">Id to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByProviderKey(object id)
        {
            var asGuid = id.TryConvertTo<Guid>();
            if (asGuid.Success)
            {
                return GetByKey((Guid)id);
            }
            var asInt = id.TryConvertTo<int>();
            if (asInt.Success)
            {
                return GetById((int)id);
            }

            return null;
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by email
        /// </summary>
        /// <param name="email">Email to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByEmail(string email)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                var query = Query<IMember>.Builder.Where(x => x.Email.Equals(email));
                var member = repository.GetByQuery(query).FirstOrDefault();

                return member;
            }
        }

        /// <summary>
        /// Get an <see cref="IMember"/> by username
        /// </summary>
        /// <param name="username">Username to use for retrieval</param>
        /// <returns><see cref="IMember"/></returns>
        public IMember GetByUsername(string username)
        {
            //TODO: Somewhere in here, whether at this level or the repository level, we need to add 
            // a caching mechanism since this method is used by all the membership providers and could be
            // called quite a bit when dealing with members.

            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                var query = Query<IMember>.Builder.Where(x => x.Username.Equals(username));
                var member = repository.GetByQuery(query).FirstOrDefault();

                return member;
            }
        }

        /// <summary>
        /// Deletes an <see cref="IMember"/>
        /// </summary>
        /// <param name="member"><see cref="IMember"/> to Delete</param>
        public void Delete(IMember member)
        {
            if (Deleting.IsRaisedEventCancelled(new DeleteEventArgs<IMember>(member), this))
                return;

            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                repository.Delete(member);
                uow.Commit();

                var args = new DeleteEventArgs<IMember>(member, false);
                Deleted.RaiseEvent(args, this);

                //remove any flagged media files
                repository.DeleteMediaFiles(args.MediaFilesToDelete);
            }
        }

        /// <summary>
        /// Saves an <see cref="IMember"/>
        /// </summary>
        /// <param name="entity"><see cref="IMember"/> to Save</param>
        /// <param name="raiseEvents">Optional parameter to raise events. 
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IMember entity, bool raiseEvents = true)
        {
            if (raiseEvents)
            {
                if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(entity), this))
                {
                    return;
                }
            }

            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                repository.AddOrUpdate(entity);
                repository.AddOrUpdateContentXml(entity, m => _entitySerializer.Serialize(_dataTypeService, m));
                // generate preview for blame history?
                if (UmbracoConfig.For.UmbracoSettings().Content.GlobalPreviewStorageEnabled)
                {
                    repository.AddOrUpdatePreviewXml(entity, m => _entitySerializer.Serialize(_dataTypeService, m));
                }

                uow.Commit();
            }

            if (raiseEvents)
                Saved.RaiseEvent(new SaveEventArgs<IMember>(entity, false), this);
        }

        /// <summary>
        /// Saves a list of <see cref="IMember"/> objects
        /// </summary>
        /// <param name="entities"><see cref="IEnumerable{IMember}"/> to save</param>
        /// <param name="raiseEvents">Optional parameter to raise events. 
        /// Default is <c>True</c> otherwise set to <c>False</c> to not raise events</param>
        public void Save(IEnumerable<IMember> entities, bool raiseEvents = true)
        {
            var asArray = entities.ToArray();

            if (raiseEvents)
            {
                if (Saving.IsRaisedEventCancelled(new SaveEventArgs<IMember>(asArray), this))
                    return;
            }
            using (new WriteLock(Locker))
            {
                var uow = UowProvider.GetUnitOfWork();
                using (var repository = RepositoryFactory.CreateMemberRepository(uow))
                {
                    foreach (var member in asArray)
                    {
                        repository.AddOrUpdate(member);
                        repository.AddOrUpdateContentXml(member, m => _entitySerializer.Serialize(_dataTypeService, m));
                        // generate preview for blame history?
                        if (UmbracoConfig.For.UmbracoSettings().Content.GlobalPreviewStorageEnabled)
                        {
                            repository.AddOrUpdatePreviewXml(member, m => _entitySerializer.Serialize(_dataTypeService, m));
                        }
                    }

                    //commit the whole lot in one go
                    uow.Commit();
                }

                if (raiseEvents)
                    Saved.RaiseEvent(new SaveEventArgs<IMember>(asArray, false), this);
            }
        }

        #endregion

        #region IMembershipRoleService Implementation

        public void AddRole(string roleName)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                repository.CreateIfNotExists(roleName);
            }
        }

        public IEnumerable<string> GetAllRoles()
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                var result = repository.GetAll();
                return result.Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<string> GetAllRoles(int memberId)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                var result = repository.GetMemberGroupsForMember(memberId);
                return result.Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<string> GetAllRoles(string username)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                var result = repository.GetMemberGroupsForMember(username);
                return result.Select(x => x.Name).Distinct();
            }
        }

        public IEnumerable<IMember> GetMembersInRole(string roleName)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                return repository.GetByMemberGroup(roleName);
            }
        }

        public IEnumerable<IMember> FindMembersInRole(string roleName, string usernameToMatch, StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberRepository(uow))
            {
                return repository.FindMembersInRole(roleName, usernameToMatch, matchType);
            }
        }

        public bool DeleteRole(string roleName, bool throwIfBeingUsed)
        {
            using (new WriteLock(Locker))
            {
                if (throwIfBeingUsed)
                {
                    var inRole = GetMembersInRole(roleName);
                    if (inRole.Any())
                    {
                        throw new InvalidOperationException("The role " + roleName + " is currently assigned to members");
                    }
                }

                var uow = UowProvider.GetUnitOfWork();
                using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
                {
                    var qry = new Query<IMemberGroup>().Where(g => g.Name == roleName);
                    var found = repository.GetByQuery(qry).ToArray();

                    foreach (var memberGroup in found)
                    {
                        _memberGroupService.Delete(memberGroup);
                    }
                    return found.Any();
                }
            }
        }
        public void AssignRole(string username, string roleName)
        {
            AssignRoles(new[] { username }, new[] { roleName });
        }

        public void AssignRoles(string[] usernames, string[] roleNames)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                repository.AssignRoles(usernames, roleNames);
            }
        }

        public void DissociateRole(string username, string roleName)
        {
            DissociateRoles(new[] { username }, new[] { roleName });
        }

        public void DissociateRoles(string[] usernames, string[] roleNames)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                repository.DissociateRoles(usernames, roleNames);
            }
        }

        public void AssignRole(int memberId, string roleName)
        {
            AssignRoles(new[] { memberId }, new[] { roleName });
        }

        public void AssignRoles(int[] memberIds, string[] roleNames)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                repository.AssignRoles(memberIds, roleNames);
            }
        }

        public void DissociateRole(int memberId, string roleName)
        {
            DissociateRoles(new[] { memberId }, new[] { roleName });
        }

        public void DissociateRoles(int[] memberIds, string[] roleNames)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var repository = RepositoryFactory.CreateMemberGroupRepository(uow))
            {
                repository.DissociateRoles(memberIds, roleNames);
            }
        }



        #endregion

        private IMemberType FindMemberTypeByAlias(string memberTypeAlias)
        {
            using (var repository = RepositoryFactory.CreateMemberTypeRepository(UowProvider.GetUnitOfWork()))
            {
                var query = Query<IMemberType>.Builder.Where(x => x.Alias == memberTypeAlias);
                var types = repository.GetByQuery(query);

                if (types.Any() == false)
                    throw new Exception(
                        string.Format("No MemberType matching the passed in Alias: '{0}' was found",
                                      memberTypeAlias));

                var contentType = types.First();

                if (contentType == null)
                    throw new Exception(string.Format("MemberType matching the passed in Alias: '{0}' was null",
                                                      memberTypeAlias));

                return contentType;
            }
        }

        private void Audit(AuditType type, string message, int userId, int objectId)
        {
            var uow = UowProvider.GetUnitOfWork();
            using (var auditRepo = RepositoryFactory.CreateAuditRepository(uow))
            {
                auditRepo.AddOrUpdate(new AuditItem(objectId, message, type, userId));
                uow.Commit();
            }
        }

        #region Event Handlers

        /// <summary>
        /// Occurs before Delete
        /// </summary>
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IMemberService, DeleteEventArgs<IMember>> Deleted;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saving;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Member object has been created, but might not have been saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IMemberService, NewEventArgs<IMember>> Created;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IMemberService, SaveEventArgs<IMember>> Saved;

        #endregion

        /// <summary>
        /// A helper method that will create a basic/generic member for use with a generic membership provider
        /// </summary>
        /// <returns></returns>
        internal static IMember CreateGenericMembershipProviderMember(string name, string email, string username, string password)
        {
            var identity = int.MaxValue;

            var memType = new MemberType(-1);
            var propGroup = new PropertyGroup
                {
                    Name = "Membership",
                    Id = --identity
                };
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TextboxAlias, DataTypeDatabaseType.Ntext, Constants.Conventions.Member.Comments)
                {
                    Name = Constants.Conventions.Member.CommentsLabel,
                    SortOrder = 0,
                    Id = --identity,
                    Key = identity.ToGuid()
                });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsApproved)
                {
                    Name = Constants.Conventions.Member.IsApprovedLabel,
                    SortOrder = 3,
                    Id = --identity,
                    Key = identity.ToGuid()
                });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.TrueFalseAlias, DataTypeDatabaseType.Integer, Constants.Conventions.Member.IsLockedOut)
                {
                    Name = Constants.Conventions.Member.IsLockedOutLabel,
                    SortOrder = 4,
                    Id = --identity,
                    Key = identity.ToGuid()
                });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLockoutDate)
                {
                    Name = Constants.Conventions.Member.LastLockoutDateLabel,
                    SortOrder = 5,
                    Id = --identity,
                    Key = identity.ToGuid()
                });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastLoginDate)
                {
                    Name = Constants.Conventions.Member.LastLoginDateLabel,
                    SortOrder = 6,
                    Id = --identity,
                    Key = identity.ToGuid()
                });
            propGroup.PropertyTypes.Add(new PropertyType(Constants.PropertyEditors.NoEditAlias, DataTypeDatabaseType.Date, Constants.Conventions.Member.LastPasswordChangeDate)
                {
                    Name = Constants.Conventions.Member.LastPasswordChangeDateLabel,
                    SortOrder = 7,
                    Id = --identity,
                    Key = identity.ToGuid()
                });

            memType.PropertyGroups.Add(propGroup);

            var member = new Member(name, email, username, password, memType);

            //we've assigned ids to the property types and groups but we also need to assign fake ids to the properties themselves.
            foreach (var property in member.Properties)
            {
                property.Id = --identity;
            }

            return member;
        }
    }
}