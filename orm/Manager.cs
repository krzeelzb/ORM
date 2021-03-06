﻿using System;
using System.Collections.Generic;
using System.Text;
using orm.Connection;
using orm.Attributes;
using orm.Mapper;
using System.Data.SqlClient;
using System.Reflection;
using orm.Query;
using orm.Relationships;
using System.Linq;
using System.Collections;
using orm.Criterias;


namespace orm
{
    class Manager
    {
        private MSSqlConnection _connection;
        private PropertiesMapper _propertiesMapper;
        private RelationshipsMapper _relationshipsMapper;
        private List<string> _queries;
        private List<Tuple<string, object>> _valuesToSet;
        private String operation;
        public Manager(MSSqlConnection connection)
        {
            _connection = connection;
            _propertiesMapper = new PropertiesMapper();
            _relationshipsMapper = new RelationshipsMapper();
            _queries = new List<string>();
        }

        public void insert(Object obj)
        {

            List<Tuple<string, object>> columnsAndValuesList = _propertiesMapper.getColumnAndValue(obj);
            object primarKey = _propertiesMapper.findPrimaryKey(obj); 
            object primaryKeyName = _propertiesMapper.findPrimaryKeyFieldName(obj);
            string tableName = _propertiesMapper.getTableName(obj);

            List<IRelationship> oneToOneRelationshipsList = _relationshipsMapper.findOneToOneRelationships(obj);
            List<IRelationship> oneToManyRelationshipsList = _relationshipsMapper.findOneToManyRelationships(obj);
            
            QueryBuilder query = new QueryBuilder();

            if (oneToOneRelationshipsList.Count == 0 && oneToManyRelationshipsList.Count == 0)
            {
                List<string> ColumnList = _propertiesMapper.getColumnName(obj);
                string insertQuery = query.createInsertQuery(tableName, columnsAndValuesList);
                _queries.Add(insertQuery);
            }
            else
            {
                handleRelationships(obj, null);
            }

            _connection.ConnectAndOpen();

            foreach (string q in _queries)
            {
                 SqlCommand command = _connection.execute(q);
                 command.ExecuteNonQuery();
            }

            _connection.Dispose();
            _queries.Clear();
        }

        public IEnumerable select(Type type, List<Criteria> listOfCriteria) {
            List<object> result = new List<object>();

            object obj = Activator.CreateInstance(type);
            string tableName = _propertiesMapper.getTableName(obj);
            string primaryKeyName = _propertiesMapper.findPrimaryKeyFieldName(obj);

            QueryBuilder queryBuilder = new QueryBuilder();
            String query = queryBuilder.createSelectQuery(tableName, listOfCriteria);
        
            _connection.ConnectAndOpen();
            SqlCommand command = _connection.execute(query);
            //command.ExecuteNonQuery();
            SqlDataReader reader = command.ExecuteReader();

            // Getting all elements that fit the criteria and putting their primary keys into listOfIds.
            LinkedList<object> listOfIds = new LinkedList<object>();
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetName(i).ToString() == primaryKeyName) {
                        listOfIds.AddLast(reader[i]);
                    }
                }
            }
            reader.Close();
            _connection.Dispose();


            // Triggering selectById for every single object that is present in listOfIds.
            foreach (object id in listOfIds) {
                result.Add(selectById(type, id));
            }
            return result; 
        }

        // Selects single object from database basing on its id (primary key).
        public object selectById(Type type, object id){
            
            object obj = Activator.CreateInstance(type);
            string tableName = _propertiesMapper.getTableName(obj);
            string primaryKeyName = _propertiesMapper.findPrimaryKeyFieldName(obj);
            object primaryKeyValue = _propertiesMapper.findPrimaryKey(obj);


            // Gets the name of the base class to create additional column.
            var nameOfBaseClass = obj.GetType().BaseType;
            if (nameOfBaseClass.GetType() == typeof(object))
            {
                nameOfBaseClass = obj.GetType();
            }
            var nameOfBaseClassWithoutNamespaces = _propertiesMapper.convertObjectNameToString(nameOfBaseClass);
            string nameOfColumnThatRepresentsThisObjectInAnotherTable = nameOfBaseClassWithoutNamespaces + "Id";

            
            QueryBuilder queryBuilder = new QueryBuilder();
            String query = queryBuilder.createSelectByIdQuery(tableName, id, primaryKeyName);

           
            List<IRelationship> oneToOneRelationshipsList = _relationshipsMapper.findOneToOneRelationships(obj);
            List<IRelationship> oneToManyRelationshipsList = _relationshipsMapper.findOneToManyRelationships(obj);
            
            if (oneToOneRelationshipsList.Count == 0 && oneToManyRelationshipsList.Count == 0)
            {
                _connection.ConnectAndOpen();
                SqlCommand command = _connection.execute(query);
                //command.ExecuteNonQuery();
                SqlDataReader reader = command.ExecuteReader();

                // Wywołanie pojedyncznego selecta i podpisanie wszystkich typów generycznych.
                var mappedObject = _propertiesMapper.mapTableIntoObject(obj, reader);

                _connection.Dispose();
            }
            else{
                PropertyInfo[] props = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (PropertyInfo prp in props)
                {
                    MethodInfo strGetter = prp.GetGetMethod(nonPublic: true); 
                    object[] attOneToOne = prp.GetCustomAttributes(typeof(OneToOneAttribute), false);
                    object[] attOneToMany = prp.GetCustomAttributes(typeof(OneToManyAttribute), false);

                   var val = strGetter.Invoke(obj, null);
                   if (attOneToMany.Length != 0) {

                        IList oneToManyObjectList = new List<Type>();
                        IList list = Activator.CreateInstance(prp.PropertyType) as IList;
                        Type objectType = list.GetType().GetGenericArguments().Single();

                        List<Criteria> criterias = new List<Criteria>();
                        Criteria criteria = new Criteria();
                        criterias.Add(Criteria.equals(nameOfColumnThatRepresentsThisObjectInAnotherTable, id));
                        
                        oneToManyObjectList = (List<object>)select(objectType, criterias);
                        obj = _propertiesMapper.setCertainListField(obj, oneToManyObjectList, prp);
                    }

                    if (attOneToOne.Length == 0)
                    {
                        continue;
                    }
                    _connection.ConnectAndOpen();
                    SqlCommand commandTmp = _connection.execute(query);
                    SqlDataReader readerTmp = commandTmp.ExecuteReader();
                    
                    object foreignKeyId = _propertiesMapper.getValueOfForeignKey(prp, readerTmp); // readerTmp is closed inside of this function.
                    _connection.Dispose();
                    // Handles case, when object doesn't exists in database.
                    if (foreignKeyId == null) {
                        return null;
                    }
                    // Handles case, when object is a null in database.
                    if (foreignKeyId == DBNull.Value) {
                        obj = _propertiesMapper.setCertainField(obj, null, prp);
                        continue;
                    }

                    object mappedObject = selectById(prp.PropertyType, foreignKeyId);
                    obj = _propertiesMapper.setCertainField(obj, mappedObject, prp);

                }
                _connection.ConnectAndOpen();
                SqlCommand command = _connection.execute(query);
                //command.ExecuteNonQuery();
                SqlDataReader reader = command.ExecuteReader();
                obj = _propertiesMapper.mapTableIntoObject(obj, reader); // Reader is closed inside of this function.
                _connection.Dispose();
            }

            return obj;
        }
        
        public void update(Type type, List<Tuple<string, object>> valuesToSet, List<Criteria> criterias)
        {
            _valuesToSet = valuesToSet;

            object obj = Activator.CreateInstance(type);

            List<IRelationship> oneToOneRelationshipsList = _relationshipsMapper.findOneToOneRelationships(obj);

            PropertyInfo[] props = type.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (PropertyInfo prp in props)
            {
                MethodInfo strGetter = prp.GetGetMethod(nonPublic: true); //get id()  (column)
                object[] attOneToMany = prp.GetCustomAttributes(typeof(OneToManyAttribute), false);
                object[] attOneToOne = prp.GetCustomAttributes(typeof(OneToOneAttribute), false);
                object[] attColumn = prp.GetCustomAttributes(typeof(ColumnAttribute), false);

                if (attOneToOne.Count() > 0 && attColumn.Count()>0) {
                    string colName="";
                    foreach (ColumnAttribute o in attColumn)
                    {
                        if (o.ColumnName == null)
                        {
                            colName = _propertiesMapper.convertObjectNameToString(prp.Name);
                        }else{
                            colName = o.ColumnName;
                        }
                    }

                    for (int it = 0; it < _valuesToSet.Count(); it++)
                    {
                        if (_valuesToSet[it].Item1.Equals(colName))
                        {
                            _valuesToSet[it] = new Tuple<string, object>
                                (colName, _propertiesMapper.findPrimaryKey(_valuesToSet[it].Item2));
                        }
                    }
                }
            }

            string tableName = _propertiesMapper.getTableName(obj);
            List<string> ColumnList = _propertiesMapper.getColumnName(obj);
            List<Tuple<string, object>> columnsAndValuesList = _propertiesMapper.getColumnAndValue(obj);

            QueryBuilder query = new QueryBuilder();
            string updateQuery = query.createUpdateQuery(tableName, _valuesToSet, criterias);
            _queries.Add(updateQuery);

            _connection.ConnectAndOpen();
            foreach (string q in _queries)
            {
                SqlCommand command = _connection.execute(q);
                command.ExecuteNonQuery();
            }
            _connection.Dispose();
            _queries.Clear();
        }

        public void delete(Type type, List<Criteria> listOfCriteria)
        {
            object obj = Activator.CreateInstance(type);
            string tableName = _propertiesMapper.getTableName(obj);
            string primaryKeyName = _propertiesMapper.findPrimaryKeyFieldName(obj);

            QueryBuilder queryBuilder = new QueryBuilder();
            String query = queryBuilder.createDeleteQuery(tableName, listOfCriteria);
            
            _connection.ConnectAndOpen();
            SqlCommand command = _connection.execute(query);
            command.ExecuteNonQuery();
            _connection.Dispose();
        }
                
        public void handleRelationships(object obj, object parent)
        {
            string tableName = _propertiesMapper.getTableName(obj);
            List<string> ColumnList = _propertiesMapper.getColumnName(obj);
            List<Tuple<string, object>> columnsAndValuesList = _propertiesMapper.getColumnAndValue(obj);

            // This part is responsible for adding foreign key in child object. (Cat receives person's ID).
            if (parent != null)
            {
                // Gets name of the column, which contains parent's ID.
                var nameOfBaseClass = parent.GetType().BaseType;
                if (nameOfBaseClass == typeof(object)) {
                    nameOfBaseClass = parent.GetType();
                }
                var nameOfBaseClassWithoutNamespaces = _propertiesMapper.convertObjectNameToString(nameOfBaseClass);
                string foreignColumn = nameOfBaseClassWithoutNamespaces;

                foreignColumn += "Id";
                object parentId = _propertiesMapper.findPrimaryKey(parent); // Gets foreign key.
                columnsAndValuesList.Add(new Tuple<string, object>(foreignColumn, parentId));
            }

            List<IRelationship> oneToOneRelationshipsList = _relationshipsMapper.findOneToOneRelationships(obj);
            List<IRelationship> oneToManyRelationshipsList = _relationshipsMapper.findOneToManyRelationships(obj);


            QueryBuilder query = new QueryBuilder();
            foreach (OneToOneRelationship rel in oneToOneRelationshipsList)
            {
                handleRelationships(rel.getOwned(), null);
            }
            foreach (OneToManyRelationship rel in oneToManyRelationshipsList)
            {

                Type listType = rel.getOwnedType();
                IEnumerable e = rel.getOwned() as IEnumerable;
                foreach (object child in e)
                {
                    handleRelationships(child, obj);
                }
            }

            string insertQuery = query.createInsertQuery(tableName, columnsAndValuesList);
            _queries.Add(insertQuery);

        }

        public static object ConvertList(List<Object> value, Type type)
        {
            var containedType = type.GenericTypeArguments.First();
            return value.Select(item => Convert.ChangeType(item, containedType)).ToList();
        }

        public void createTable(object obj)
        {
            List<Tuple<string, object>> columnsAndValuesList = _propertiesMapper.getColumnAndValue(obj);
            object primaryKeyName = _propertiesMapper.findPrimaryKeyFieldName(obj);
            string tableName = _propertiesMapper.getTableName(obj);

            QueryBuilder q = new QueryBuilder();
            string query = q.createCreateTableQuery(tableName, columnsAndValuesList, primaryKeyName);

            _connection.ConnectAndOpen();
            SqlCommand command = _connection.execute(query);
            command.ExecuteNonQuery();
            _connection.Dispose();
        }
    }
}
